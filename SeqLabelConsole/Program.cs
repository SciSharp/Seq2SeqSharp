﻿using AdvUtils;
using Newtonsoft.Json;
using Seq2SeqSharp;
using Seq2SeqSharp.Metrics;
using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeqLabelConsole
{
    internal class Program
    {
        private static void ss_IterationDone(object sender, EventArgs e)
        {
            CostEventArg ep = e as CostEventArg;

            if (float.IsInfinity(ep.CostPerWord) == false)
            {
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

                Logger.WriteLine($"Update = {ep.Update}, Epoch = {ep.Epoch}, LR = {ep.LearningRate.ToString("F6")}, Cost = {ep.CostPerWord.ToString("F4")}, AvgCost = {ep.AvgCostInTotal.ToString("F4")}, Sent = {ep.ProcessedSentencesInTotal}, SentPerMin = {sentPerMin.ToString("F")}, WordPerSec = {wordPerSec.ToString("F")}");
            }

        }

        public static string GetTimeStamp(DateTime timeStamp)
        {
            return string.Format("{0:yyyy}_{0:MM}_{0:dd}_{0:HH}h_{0:mm}m_{0:ss}s", timeStamp);
        }

        private static void Main(string[] args)
        {
            ShowOptions(args);

            Logger.LogFile = $"{nameof(SeqLabelConsole)}_{GetTimeStamp(DateTime.Now)}.log";

            //Parse command line
            Options opts = new Options();
            ArgParser argParser = new ArgParser(args, opts);

            if (string.IsNullOrEmpty(opts.ConfigFilePath) == false)
            {
                Logger.WriteLine($"Loading config file from '{opts.ConfigFilePath}'");
                opts = JsonConvert.DeserializeObject<Options>(File.ReadAllText(opts.ConfigFilePath));
            }


            SequenceLabel sl = null;
            ProcessorTypeEnums processorType = (ProcessorTypeEnums)Enum.Parse(typeof(ProcessorTypeEnums), opts.ProcessorType);
            EncoderTypeEnums encoderType = (EncoderTypeEnums)Enum.Parse(typeof(EncoderTypeEnums), opts.EncoderType);
            ModeEnums mode = (ModeEnums)Enum.Parse(typeof(ModeEnums), opts.TaskName);

            //Parse device ids from options          
            int[] deviceIds = opts.DeviceIds.Split(',').Select(x => int.Parse(x)).ToArray();
            if (mode == ModeEnums.Train)
            {
                // Load train corpus
                SequenceLabelingCorpus trainCorpus = new SequenceLabelingCorpus(opts.TrainCorpusPath, opts.BatchSize, opts.ShuffleBlockSize, maxSentLength: opts.MaxSentLength);

                // Load valid corpus
                SequenceLabelingCorpus validCorpus = string.IsNullOrEmpty(opts.ValidCorpusPath) ? null : new SequenceLabelingCorpus(opts.ValidCorpusPath, opts.BatchSize, opts.ShuffleBlockSize, maxSentLength: opts.MaxSentLength);

                // Load or build vocabulary
                Vocab vocab = null;
                if (!string.IsNullOrEmpty(opts.SrcVocab) && !string.IsNullOrEmpty(opts.TgtVocab))
                {
                    // Vocabulary files are specified, so we load them
                    vocab = new Vocab(opts.SrcVocab, opts.TgtVocab);
                }
                else
                {
                    // We don't specify vocabulary, so we build it from train corpus
                    vocab = new Vocab(trainCorpus);
                }

                // Create learning rate
                ILearningRate learningRate = new DecayLearningRate(opts.StartLearningRate, opts.WarmUpSteps, opts.WeightsUpdateCount);

                // Create optimizer
                AdamOptimizer optimizer = new AdamOptimizer(opts.GradClip, opts.Beta1, opts.Beta2);

                // Create metrics
                List<IMetric> metrics = new List<IMetric>();
                foreach (string word in vocab.TgtVocab)
                {
                    metrics.Add(new SequenceLabelFscoreMetric(word));
                }

                if (File.Exists(opts.ModelFilePath) == false)
                {
                    //New training
                    sl = new SequenceLabel(hiddenDim: opts.HiddenSize, embeddingDim: opts.WordVectorSize, encoderLayerDepth: opts.EncoderLayerDepth, multiHeadNum: opts.MultiHeadNum,
                        encoderType: encoderType,
                        dropoutRatio: opts.DropoutRatio, deviceIds: deviceIds, processorType: processorType, modelFilePath: opts.ModelFilePath, vocab: vocab, maxSntSize: opts.MaxSentLength);
                }
                else
                {
                    //Incremental training
                    Logger.WriteLine($"Loading model from '{opts.ModelFilePath}'...");
                    sl = new SequenceLabel(modelFilePath: opts.ModelFilePath, processorType: processorType, deviceIds: deviceIds, dropoutRatio: opts.DropoutRatio, maxSntSize: opts.MaxSentLength);
                }

                // Add event handler for monitoring
                sl.IterationDone += ss_IterationDone;

                // Kick off training
                sl.Train(maxTrainingEpoch: opts.MaxEpochNum, trainCorpus: trainCorpus, validCorpus: validCorpus, learningRate: learningRate, optimizer: optimizer, metrics: metrics);


            }
            else if (mode == ModeEnums.Valid)
            {
                Logger.WriteLine($"Evaluate model '{opts.ModelFilePath}' by valid corpus '{opts.ValidCorpusPath}'");

                // Load valid corpus
                SequenceLabelingCorpus validCorpus = new SequenceLabelingCorpus(opts.ValidCorpusPath, opts.BatchSize, opts.ShuffleBlockSize, opts.MaxSentLength);

                Vocab vocab = new Vocab(validCorpus);
                // Create metrics
                List<IMetric> metrics = new List<IMetric>();
                foreach (string word in vocab.TgtVocab)
                {
                    metrics.Add(new SequenceLabelFscoreMetric(word));
                }

                sl = new SequenceLabel(modelFilePath: opts.ModelFilePath, processorType: processorType, deviceIds: deviceIds, maxSntSize: opts.MaxSentLength);
                sl.Valid(validCorpus: validCorpus, metrics: metrics);
            }
            else if (mode == ModeEnums.Test)
            {
                Logger.WriteLine($"Test model '{opts.ModelFilePath}' by input corpus '{opts.InputTestFile}'");

                //Test trained model
                sl = new SequenceLabel(modelFilePath: opts.ModelFilePath, processorType: processorType, deviceIds: deviceIds, maxSntSize: opts.MaxSentLength);

                List<string> outputLines = new List<string>();
                string[] data_sents_raw1 = File.ReadAllLines(opts.InputTestFile);
                foreach (string line in data_sents_raw1)
                {
                    List<List<string>> outputTokensBatch = sl.Test(ParallelCorpus.ConstructInputTokens(line.ToLower().Trim().Split(' ').ToList(), false));
                    outputLines.AddRange(outputTokensBatch.Select(x => string.Join(" ", x)));
                }

                File.WriteAllLines(opts.OutputTestFile, outputLines);
            }
            else
            {
                argParser.Usage();
            }
        }

        private static void ShowOptions(string[] args)
        {
            string commandLine = string.Join(" ", args);
            Logger.WriteLine($"Seq2SeqSharp v2.0 written by Zhongkai Fu(fuzhongkai@gmail.com)");
            Logger.WriteLine($"Command Line = '{commandLine}'");
        }
    }
}
