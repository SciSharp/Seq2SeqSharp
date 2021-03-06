﻿using AdvUtils;
using Seq2SeqSharp.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Seq2SeqSharp.Tools
{
    public class SequenceLabelingCorpus : IEnumerable<SntPairBatch>
    {
        private readonly int m_maxSentLength = 32;

        private readonly int m_blockSize = 1000000;
        private readonly int m_batchSize = 1;
        private readonly bool m_addBOSEOS = true;
        private readonly List<string> m_srcFileList;
        private readonly List<string> m_tgtFileList;

        public int CorpusSize = 0;

        public int BatchSize => m_batchSize;

        public const string EOS = "<END>";
        public const string BOS = "<START>";
        public const string UNK = "<UNK>";

        private bool m_showTokenDist = true;

        public static bool IsPreDefinedToken(string str)
        {
            return str == EOS || str == BOS || str == UNK;
        }

        private readonly Random rnd = new Random(DateTime.Now.Millisecond);

        private void Shuffle(List<RawSntPair> rawSntPairs)
        {

            //Put sentence pair with same source length into the bucket
            Dictionary<int, List<RawSntPair>> dict = new Dictionary<int, List<RawSntPair>>(); //<source sentence length, sentence pair set>
            foreach (RawSntPair item in rawSntPairs)
            {
                int length = item.SrcLength;

                if (dict.ContainsKey(length) == false)
                {
                    dict.Add(length, new List<RawSntPair>());
                }
                dict[length].Add(item);
            }

            //Randomized the order of sentence pairs with same length in source side
            Parallel.ForEach(dict, pair =>
            //foreach (KeyValuePair<int, List<SntPair>> pair in dict)
            {
                Random rnd2 = new Random(DateTime.Now.Millisecond + pair.Key);

                List<RawSntPair> sntPairList = pair.Value;
                for (int i = 0; i < sntPairList.Count; i++)
                {
                    int idx = rnd2.Next(0, sntPairList.Count);
                    RawSntPair tmp = sntPairList[i];
                    sntPairList[i] = sntPairList[idx];
                    sntPairList[idx] = tmp;
                }
            });

            SortedDictionary<int, List<RawSntPair>> sdict = new SortedDictionary<int, List<RawSntPair>>(); //<The bucket size, sentence pair set>
            foreach (KeyValuePair<int, List<RawSntPair>> pair in dict)
            {
                if (sdict.ContainsKey(pair.Value.Count) == false)
                {
                    sdict.Add(pair.Value.Count, new List<RawSntPair>());
                }
                sdict[pair.Value.Count].AddRange(pair.Value);
            }

            rawSntPairs.Clear();

            int[] keys = sdict.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                int idx = rnd.Next(0, keys.Length);
                int tmp = keys[i];
                keys[i] = keys[idx];
                keys[idx] = tmp;
            }

            foreach (int key in keys)
            {
                rawSntPairs.AddRange(sdict[key]);
            }

        }

        private (string, string) ShuffleAll()
        {
            SortedDictionary<int, int> dictSrcLenDist = new SortedDictionary<int, int>();
            SortedDictionary<int, int> dictTgtLenDist = new SortedDictionary<int, int>();

            string srcShuffledFilePath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName() + ".tmp");
            string tgtShuffledFilePath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName() + ".tmp");

            Logger.WriteLine($"Shuffling corpus for '{m_srcFileList.Count}' files.");

            StreamWriter swSrc = new StreamWriter(srcShuffledFilePath, false);
            StreamWriter swTgt = new StreamWriter(tgtShuffledFilePath, false);

            List<RawSntPair> sntPairs = new List<RawSntPair>();
            CorpusSize = 0;
            int tooLongSrcSntCnt = 0;

            for (int i = 0; i < m_srcFileList.Count; i++)
            {
                if (m_showTokenDist)
                {
                    Logger.WriteLine($"Process file '{m_srcFileList[i]}' and '{m_tgtFileList[i]}'");
                }

                StreamReader srSrc = new StreamReader(m_srcFileList[i]);
                StreamReader srTgt = new StreamReader(m_tgtFileList[i]);

                while (true)
                {
                    if (srSrc.EndOfStream && srTgt.EndOfStream)
                    {
                        break;
                    }

                    RawSntPair rawSntPair = new RawSntPair(srSrc.ReadLine(), srTgt.ReadLine());
                    if (rawSntPair.IsEmptyPair())
                    {
                        break;
                    }

                    if (dictSrcLenDist.ContainsKey(rawSntPair.SrcLength / 100) == false)
                    {
                        dictSrcLenDist.Add(rawSntPair.SrcLength / 100, 0);
                    }
                    dictSrcLenDist[rawSntPair.SrcLength / 100]++;

                    if (dictTgtLenDist.ContainsKey(rawSntPair.TgtLength / 100) == false)
                    {
                        dictTgtLenDist.Add(rawSntPair.TgtLength / 100, 0);
                    }
                    dictTgtLenDist[rawSntPair.TgtLength / 100]++;


                    bool hasTooLongSent = false;
                    if (rawSntPair.SrcLength > m_maxSentLength)
                    {
                        tooLongSrcSntCnt++;
                        hasTooLongSent = true;
                    }

                    if (hasTooLongSent)
                    {
                        continue;
                    }

                    sntPairs.Add(rawSntPair);
                    CorpusSize++;
                    if (m_blockSize > 0 && sntPairs.Count >= m_blockSize)
                    {
                        Shuffle(sntPairs);
                        foreach (RawSntPair item in sntPairs)
                        {
                            swSrc.WriteLine(item.SrcSnt);
                            swTgt.WriteLine(item.TgtSnt);
                        }
                        sntPairs.Clear();
                    }
                }

                srSrc.Close();
                srTgt.Close();
            }

            if (sntPairs.Count > 0)
            {
                Shuffle(sntPairs);
                foreach (RawSntPair item in sntPairs)
                {
                    swSrc.WriteLine(item.SrcSnt);
                    swTgt.WriteLine(item.TgtSnt);
                }

                sntPairs.Clear();
            }


            swSrc.Close();
            swTgt.Close();

            Logger.WriteLine($"Shuffled '{CorpusSize}' sentence pairs to file '{srcShuffledFilePath}' and '{tgtShuffledFilePath}'.");

            if (tooLongSrcSntCnt > 0)
            {
                Logger.WriteLine(Logger.Level.warn, ConsoleColor.Yellow, $"Found {tooLongSrcSntCnt} source sentences are longer than '{m_maxSentLength}' tokens, ignore them.");
            }

            if (m_showTokenDist)
            {
                Logger.WriteLine($"Src token length distribution");

                int srcTotalNum = 0;
                foreach (var pair in dictSrcLenDist)
                {
                    srcTotalNum += pair.Value;
                }

                int srcAccNum = 0;
                foreach (var pair in dictSrcLenDist)
                {
                    srcAccNum += pair.Value;

                    Logger.WriteLine($"{pair.Key * 100} ~ {(pair.Key + 1) * 100}: {pair.Value} (acc: {(100.0f * (float)srcAccNum / (float)srcTotalNum).ToString("F")}%)");
                }

                Logger.WriteLine($"Tgt token length distribution");

                int tgtTotalNum = 0;
                foreach (var pair in dictTgtLenDist)
                {
                    tgtTotalNum += pair.Value;
                }

                int tgtAccNum = 0;

                foreach (var pair in dictTgtLenDist)
                {
                    tgtAccNum += pair.Value;

                    Logger.WriteLine($"{pair.Key * 100} ~ {(pair.Key + 1) * 100}: {pair.Value}  (acc: {(100.0f * (float)tgtAccNum / (float)tgtTotalNum).ToString("F")}%)");
                }

                m_showTokenDist = false;
            }


            return (srcShuffledFilePath, tgtShuffledFilePath);
        }

        public IEnumerator<SntPairBatch> GetEnumerator()
        {
            (string srcShuffledFilePath, string tgtShuffledFilePath) = ShuffleAll();

            using (StreamReader srSrc = new StreamReader(srcShuffledFilePath))
            {
                using (StreamReader srTgt = new StreamReader(tgtShuffledFilePath))
                {
                    int lastSrcSntLen = -1;
                    int lastTgtSntLen = -1;
                    int maxOutputsSize = m_batchSize * 10000;
                    List<SntPair> outputs = new List<SntPair>();

                    while (true)
                    {
                        string line;
                        SntPair sntPair = new SntPair();
                        if ((line = srSrc.ReadLine()) == null)
                        {
                            break;
                        }

                        line = line.ToLower().Trim();
                        if (m_addBOSEOS)
                        {
                            line = $"{BOS} {line} {EOS}";
                        }
                        sntPair.SrcSnt = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        line = srTgt.ReadLine().ToLower().Trim();
                        if (m_addBOSEOS)
                        {
                            line = $"{BOS} {line} {EOS}";
                        }
                        sntPair.TgtSnt = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);


                        if ((lastSrcSntLen > 0 && lastSrcSntLen != sntPair.SrcSnt.Length) ||
                            outputs.Count > maxOutputsSize)
                        {
                            // InnerShuffle(outputs);
                            for (int i = 0; i < outputs.Count; i += m_batchSize)
                            {
                                int size = Math.Min(m_batchSize, outputs.Count - i);
                                yield return new SntPairBatch(outputs.GetRange(i, size));
                            }

                            outputs.Clear();
                        }

                        outputs.Add(sntPair);

                        lastSrcSntLen = sntPair.SrcSnt.Length;
                        lastTgtSntLen = sntPair.TgtSnt.Length;
                    }

                    // InnerShuffle(outputs);
                    for (int i = 0; i < outputs.Count; i += m_batchSize)
                    {
                        int size = Math.Min(m_batchSize, outputs.Count - i);
                        yield return new SntPairBatch(outputs.GetRange(i, size));
                    }
                }
            }

            File.Delete(srcShuffledFilePath);
            File.Delete(tgtShuffledFilePath);
        }

        /// <summary>
        /// Pad given sentences to the same length and return their original length
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static List<int> PadSentences(List<List<string>> s, int maxLen = -1)
        {
            List<int> originalLengths = new List<int>();

            if (maxLen <= 0)
            {
                foreach (List<string> item in s)
                {
                    if (item.Count > maxLen)
                    {
                        maxLen = item.Count;
                    }
                }
            }

            for (int i = 0; i < s.Count; i++)
            {
                int count = s[i].Count;
                originalLengths.Add(count);

                for (int j = 0; j < maxLen - count; j++)
                {
                    s[i].Add(ParallelCorpus.EOS);
                }
            }

            return originalLengths;
        }


        public static List<List<string>> LeftShiftSnts(List<List<string>> input, string lastTokenToPad)
        {
            List<List<string>> r = new List<List<string>>();

            foreach (var seq in input)
            {
                List<string> rseq = new List<string>();

                rseq.AddRange(seq);
                rseq.RemoveAt(0);
                rseq.Add(lastTokenToPad);

                r.Add(rseq);
            }

            return r;
        }


        /// <summary>
        /// Shuffle given sentence pairs and return the length of the longgest source sentence
        /// </summary>
        /// <param name="sntPairs"></param>
        /// <returns></returns>
        //private int InnerShuffle(List<SntPair> sntPairs)
        //{
        //    int maxSrcLen = 0;
        //    for (int i = 0; i < sntPairs.Count; i++)
        //    {
        //        if (sntPairs[i].SrcSnt.Length > maxSrcLen)
        //        {
        //            maxSrcLen = sntPairs[i].SrcSnt.Length;
        //        }

        //        int idx = rnd.Next(0, sntPairs.Count);
        //        SntPair tmp = sntPairs[i];
        //        sntPairs[i] = sntPairs[idx];
        //        sntPairs[idx] = tmp;
        //    }

        //    return maxSrcLen;
        //}

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private (string, string) ConvertSequenceLabelingFormatToParallel(string filePath)
        {
            List<string> srcLines = new List<string>();
            List<string> tgtLines = new List<string>();

            List<string> currSrcLine = new List<string>();
            List<string> currTgtLine = new List<string>();
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (String.IsNullOrEmpty(line) == true)
                {
                    //This is a new record

                    srcLines.Add(String.Join(" ", currSrcLine));
                    tgtLines.Add(String.Join(" ", currTgtLine));

                    currSrcLine = new List<string>();
                    currTgtLine = new List<string>();
                }
                else
                {
                    string[] items = line.Split(new char[] { ' ', '\t' });
                    string srcItem = items[0];
                    string tgtItem = items[1];

                    currSrcLine.Add(srcItem);
                    currTgtLine.Add(tgtItem);
                }
            }

            srcLines.Add(String.Join(" ", currSrcLine));
            tgtLines.Add(String.Join(" ", currTgtLine));

            string srcFilePath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName() + "_src.tmp");
            string tgtFilePath = Path.Combine(Directory.GetCurrentDirectory(), Path.GetRandomFileName() + "_tgt.tmp");

            File.WriteAllLines(srcFilePath, srcLines);
            File.WriteAllLines(tgtFilePath, tgtLines);

            Logger.WriteLine($"Convert sequence labeling corpus file '{filePath}' to parallel corpus files '{srcFilePath}' and '{tgtFilePath}'");

            return (srcFilePath, tgtFilePath);
        }


        public SequenceLabelingCorpus(string corpusFilePath, int batchSize, int shuffleBlockSize = -1, int maxSentLength = 128)
        {
            Logger.WriteLine($"Loading sequence labeling corpus from '{corpusFilePath}' MaxSrcSentLength = '{maxSentLength}'");
            m_batchSize = batchSize;
            m_blockSize = shuffleBlockSize;
            m_maxSentLength = maxSentLength;

            m_srcFileList = new List<string>();
            m_tgtFileList = new List<string>();


            (string srcFilePath, string tgtFilePath) = ConvertSequenceLabelingFormatToParallel(corpusFilePath);

            m_srcFileList.Add(srcFilePath);
            m_tgtFileList.Add(tgtFilePath);
        }
    }
}
