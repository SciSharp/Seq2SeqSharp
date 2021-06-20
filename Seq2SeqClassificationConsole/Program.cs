﻿using AdvUtils;
using Newtonsoft.Json;
using Seq2SeqSharp;
using Seq2SeqSharp.Applications;
using Seq2SeqSharp.Corpus;
using Seq2SeqSharp.Metrics;
using Seq2SeqSharp.Optimizer;
using Seq2SeqSharp.Tools;
using Seq2SeqSharp.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Seq2SeqClassificationConsole
{
    internal class Program
    {
        private static Seq2SeqClassificationOptions opts = new Seq2SeqClassificationOptions();
        private static void ss_EvaluationWatcher(object sender, EventArgs e)
        {
            EvaluationEventArg ep = e as EvaluationEventArg;
            Logger.WriteLine(Logger.Level.info, ep.Color, ep.Message);

            if (String.IsNullOrEmpty(opts.NotifyEmail) == false)
            {
                Email.Send(ep.Title, ep.Message, opts.NotifyEmail, new string[] { opts.NotifyEmail });
            }
        }

        private static void ss_StatusUpdateWatcher(object sender, EventArgs e)
        {
            CostEventArg ep = e as CostEventArg;

            TimeSpan ts = DateTime.Now - ep.StartDateTime;
            double sentPerMin = 0;
            double wordPerSec = 0;
            if (ts.TotalMinutes > 0)
            {
                sentPerMin = ep.ProcessedSentencesInTotal / ts.TotalMinutes;
            }

            if (ts.TotalSeconds > 0)
            {
                wordPerSec = ep.ProcessedWordsInTotal / ts.TotalSeconds;
            }

            Logger.WriteLine($"Update = {ep.Update}, Epoch = {ep.Epoch}, LR = {ep.LearningRate.ToString("F6")}, AvgCost = {ep.AvgCostInTotal.ToString("F4")}, Sent = {ep.ProcessedSentencesInTotal}, SentPerMin = {sentPerMin.ToString("F")}, WordPerSec = {wordPerSec.ToString("F")}");
        }

        public static string GetTimeStamp(DateTime timeStamp)
        {
            return string.Format("{0:yyyy}_{0:MM}_{0:dd}_{0:HH}h_{0:mm}m_{0:ss}s", timeStamp);
        }

        private static void Main(string[] args)
        {
            try
            {
                Logger.LogFile = $"{nameof(Seq2SeqClassificationConsole)}_{GetTimeStamp(DateTime.Now)}.log";
                ShowOptions(args);

                //Parse command line
                //   Seq2SeqOptions opts = new Seq2SeqOptions();
                ArgParser argParser = new ArgParser(args, opts);

                if (string.IsNullOrEmpty(opts.ConfigFilePath) == false)
                {
                    Logger.WriteLine($"Loading config file from '{opts.ConfigFilePath}'");
                    opts = JsonConvert.DeserializeObject<Seq2SeqClassificationOptions>(File.ReadAllText(opts.ConfigFilePath));
                }

                string strOpts = JsonConvert.SerializeObject(opts);
                Logger.WriteLine($"Configs: {strOpts}");

                Seq2SeqClassification ss = null;
                ModeEnums mode = (ModeEnums)Enum.Parse(typeof(ModeEnums), opts.Task);
                ShuffleEnums shuffleType = (ShuffleEnums)Enum.Parse(typeof(ShuffleEnums), opts.ShuffleType);

                if (mode == ModeEnums.Train)
                {
                    // Load train corpus
                    Seq2SeqClassificationCorpus trainCorpus = new Seq2SeqClassificationCorpus(corpusFilePath: opts.TrainCorpusPath, srcLangName: opts.SrcLang, tgtLangName: opts.TgtLang, batchSize: opts.BatchSize, shuffleBlockSize: opts.ShuffleBlockSize,
                        maxSrcSentLength: opts.MaxSrcTrainSentLength, maxTgtSentLength: opts.MaxTgtTrainSentLength, shuffleEnums: shuffleType);
                    // Load valid corpus
                    Seq2SeqClassificationCorpus validCorpus = string.IsNullOrEmpty(opts.ValidCorpusPath) ? null : new Seq2SeqClassificationCorpus(opts.ValidCorpusPath, opts.SrcLang, opts.TgtLang, opts.ValBatchSize, opts.ShuffleBlockSize, opts.MaxSrcTestSentLength, opts.MaxTgtTestSentLength, shuffleEnums: shuffleType);

                    // Create learning rate
                    ILearningRate learningRate = new DecayLearningRate(opts.StartLearningRate, opts.WarmUpSteps, opts.WeightsUpdateCount);

                    // Create optimizer
                    IOptimizer optimizer = null;
                    if (String.Equals(opts.Optimizer, "Adam", StringComparison.InvariantCultureIgnoreCase))
                    {
                        optimizer = new AdamOptimizer(opts.GradClip, opts.Beta1, opts.Beta2);
                    }
                    else
                    {
                        optimizer = new RMSPropOptimizer(opts.GradClip, opts.Beta1);
                    }

                    // Create metrics
                    Dictionary<int, List<IMetric>> taskId2metrics = new Dictionary<int, List<IMetric>>();
                    List<IMetric> task0Metrics = new List<IMetric>
                    {
                        new BleuMetric(),
                        new LengthRatioMetric()
                    };

                    taskId2metrics.Add(0, task0Metrics);



                    if (!String.IsNullOrEmpty(opts.ModelFilePath) && File.Exists(opts.ModelFilePath))
                    {
                        //Incremental training
                        Logger.WriteLine($"Loading model from '{opts.ModelFilePath}'...");
                        ss = new Seq2SeqClassification(opts);

                        List<IMetric> task1Metrics = new List<IMetric>()
                        {
                            new MultiLabelsFscoreMetric("", ss.ClsVocab.Items)
                        };
                        taskId2metrics.Add(1, task1Metrics);

                    }
                    else
                    {
                        // Load or build vocabulary
                        Vocab srcVocab = null;
                        Vocab tgtVocab = null;
                        Vocab clsVocab = null;
                        if (!string.IsNullOrEmpty(opts.SrcVocab) && !string.IsNullOrEmpty(opts.TgtVocab) && !string.IsNullOrEmpty(opts.ClsVocab))
                        {
                            Logger.WriteLine($"Loading source vocabulary from '{opts.SrcVocab}' and target vocabulary from '{opts.TgtVocab}' and classification vocabulary from '{opts.ClsVocab}'. Shared vocabulary is '{opts.SharedEmbeddings}'");
                            if (opts.SharedEmbeddings == true && (opts.SrcVocab != opts.TgtVocab))
                            {
                                throw new ArgumentException("The source and target vocabularies must be identical if their embeddings are shared.");
                            }

                            // Vocabulary files are specified, so we load them
                            srcVocab = new Vocab(opts.SrcVocab);
                            tgtVocab = new Vocab(opts.TgtVocab);
                            clsVocab = new Vocab(opts.ClsVocab);
                        }
                        else
                        {
                            Logger.WriteLine($"Building vocabulary from training corpus. Shared vocabulary is '{opts.SharedEmbeddings}'");
                            // We don't specify vocabulary, so we build it from train corpus

                            (srcVocab, tgtVocab, clsVocab) = trainCorpus.BuildVocabs(opts.VocabSize, opts.SharedEmbeddings);
                        }

                        List<IMetric> task1Metrics = new List<IMetric>()
                        {
                            new MultiLabelsFscoreMetric("", clsVocab.Items)
                        };
                        taskId2metrics.Add(1, task1Metrics);

                        //New training
                        ss = new Seq2SeqClassification(opts, srcVocab, tgtVocab, clsVocab);
                    }

                    // Add event handler for monitoring
                    ss.StatusUpdateWatcher += ss_StatusUpdateWatcher;
                    ss.EvaluationWatcher += ss_EvaluationWatcher;

                    // Kick off training
                    ss.Train(maxTrainingEpoch: opts.MaxEpochNum, trainCorpus: trainCorpus, validCorpus: validCorpus, learningRate: learningRate, optimizer: optimizer, taskId2metrics: taskId2metrics);
                }
                else if (mode == ModeEnums.Valid)
                {
                    Logger.WriteLine($"Evaluate model '{opts.ModelFilePath}' by valid corpus '{opts.ValidCorpusPath}'");

                    // Create metrics
                    List<IMetric> metrics = new List<IMetric>
                {
                    new BleuMetric(),
                    new LengthRatioMetric()
                };

                    // Load valid corpus
                    Seq2SeqClassificationCorpus validCorpus = new Seq2SeqClassificationCorpus(opts.ValidCorpusPath, opts.SrcLang, opts.TgtLang, opts.ValBatchSize, opts.ShuffleBlockSize, opts.MaxSrcTestSentLength, opts.MaxTgtTestSentLength, shuffleEnums: shuffleType);

                    ss = new Seq2SeqClassification(opts);
                    ss.EvaluationWatcher += ss_EvaluationWatcher;
                    ss.Valid(validCorpus: validCorpus, metrics: metrics);
                }
                else if (mode == ModeEnums.Test)
                {
                    Logger.WriteLine($"Test model: '{opts.ModelFilePath}'");
                    Logger.WriteLine($"Test set: '{opts.InputTestFile}'");
                    Logger.WriteLine($"Max source test sentence length: '{opts.MaxSrcTestSentLength}'");
                    Logger.WriteLine($"Max target test sentence length: '{opts.MaxTgtTestSentLength}'");
                    Logger.WriteLine($"Beam search size: '{opts.BeamSearchSize}'");
                    Logger.WriteLine($"Batch size: '{opts.BatchSize}'");
                    Logger.WriteLine($"Shuffle type: '{opts.ShuffleType}'");
                    Logger.WriteLine($"Device ids: '{opts.DeviceIds}'");


                    if (File.Exists(opts.OutputFile))
                    {
                        Logger.WriteLine(Logger.Level.err, ConsoleColor.Yellow, $"Output file '{opts.OutputFile}' exist. Delete it.");
                        File.Delete(opts.OutputFile);
                    }

                    //Test trained model
                    ss = new Seq2SeqClassification(opts);
                    List<List<string>> inputBatchs = new List<List<string>>();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    foreach (string line in File.ReadLines(opts.InputTestFile))
                    {
                        List<string> tokens = line.Trim().Split(' ').ToList();
                        if (tokens.Count > opts.MaxSrcTestSentLength - 2)
                        {
                            tokens = tokens.GetRange(0, opts.MaxSrcTestSentLength - 2);
                        }
                        inputBatchs.Add(tokens);

                        if (inputBatchs.Count >= opts.BatchSize * ss.DeviceIds.Length)
                        {
                            (var outputLines, var alignments) = RunBatchTest(opts, ss, inputBatchs);
                            File.AppendAllLines(opts.OutputFile, outputLines);
                            if (opts.OutputAlignment)
                            {
                                File.AppendAllLines(opts.OutputFile + ".alignment", alignments);
                            }

                            inputBatchs.Clear();
                        }
                    }

                    if (inputBatchs.Count > 0)
                    {
                        (var outputLines, var alignments) = RunBatchTest(opts, ss, inputBatchs);
                        File.AppendAllLines(opts.OutputFile, outputLines);
                        if (opts.OutputAlignment)
                        {
                            File.AppendAllLines(opts.OutputFile + ".alignment", alignments);
                        }
                    }
                    stopwatch.Stop();

                    Logger.WriteLine($"Test mode execution time elapsed: '{stopwatch.Elapsed}'");
                }
                else if (mode == ModeEnums.DumpVocab)
                {
                    ss = new Seq2SeqClassification(opts);
                    ss.DumpVocabToFiles(opts.SrcVocab, opts.TgtVocab, opts.ClsVocab);
                }
                else
                {
                    argParser.Usage();
                }
            }
            catch (Exception err)
            {
                Logger.WriteLine($"Exception: '{err.Message}'");
                Logger.WriteLine($"Call stack: '{err.StackTrace}'");
            }
        }

        private static (List<string>, List<string>) RunBatchTest(Seq2SeqClassificationOptions opts, Seq2SeqClassification ss, List<List<string>> inputBatchs)
        {
            List<string> outputLines = new List<string>();
            List<string> alignments = new List<string>();

            List<NetworkResult> nrs = ss.Test(inputBatchs, opts.BeamSearchSize); // shape [beam size, batch size, tgt token size]

            for (int batchIdx = 0; batchIdx < inputBatchs.Count; batchIdx++)
            {
                string clsTag = nrs[1].Output[0][batchIdx][0];
                NetworkResult tgtNR = nrs[0];

                for (int beamIdx = 0; beamIdx < tgtNR.Output.Count; beamIdx++)
                {
                    outputLines.Add(clsTag + "\t" + String.Join(" ", tgtNR.Output[beamIdx][batchIdx]));

                    if (opts.OutputAlignment)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int tgtTknIdx = 0; tgtTknIdx < tgtNR.Output[beamIdx][batchIdx].Count; tgtTknIdx++)
                        {
                            int srcIdx = tgtNR.Alignment[beamIdx][batchIdx][tgtTknIdx].SrcPos;
                            float score = tgtNR.Alignment[beamIdx][batchIdx][tgtTknIdx].Score;
                            sb.Append($"{tgtTknIdx}_{srcIdx}_{score}");
                            sb.Append(" ");
                        }

                        alignments.Add(sb.ToString().Trim());
                    }
                }
            }

            return (outputLines, alignments);
        }

        private static void ShowOptions(string[] args)
        {
            string commandLine = string.Join(" ", args);
            Logger.WriteLine($"Seq2SeqSharp v2.3.0 written by Zhongkai Fu(fuzhongkai@gmail.com)");
            Logger.WriteLine($"Command Line = '{commandLine}'");
        }
    }
}
