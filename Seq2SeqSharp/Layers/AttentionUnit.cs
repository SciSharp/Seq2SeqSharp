﻿using AdvUtils;
using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Remoting.Contexts;
using System.Threading;

namespace Seq2SeqSharp
{

    public class AttentionPreProcessResult
    {
        public int[] sourceIdxs;
        public IWeightTensor rawInputs;
        public IWeightTensor uhs;
        public IWeightTensor inputsBatchFirst;

    }

    [Serializable]
    public class AttentionUnit : INeuralUnit
    {
        private readonly IWeightTensor m_V;
        private readonly IWeightTensor m_Ua;
        private readonly IWeightTensor m_bUa;
        private readonly IWeightTensor m_Wa;
        private readonly IWeightTensor m_bWa;

        private readonly string m_name;
        private readonly int m_hiddenDim;
        private readonly int m_contextDim;
        private readonly int m_deviceId;

        private bool m_enableCoverageModel;
        private readonly IWeightTensor m_Wc;
        private readonly IWeightTensor m_bWc;
        private readonly LSTMCell m_coverage;

        private readonly int k_coverageModelDim = 8;

        public AttentionUnit(string name, int hiddenDim, int contextDim, int deviceId, bool enableCoverageModel)
        {
            m_name = name;
            m_hiddenDim = hiddenDim;
            m_contextDim = contextDim;
            m_deviceId = deviceId;
            m_enableCoverageModel = enableCoverageModel;

            Logger.WriteLine($"Creating attention unit '{name}' HiddenDim = '{hiddenDim}', ContextDim = '{contextDim}', DeviceId = '{deviceId}', EnableCoverageModel = '{enableCoverageModel}'");

            m_Ua = new WeightTensor(new long[2] { contextDim, hiddenDim }, deviceId, normal: true, name: $"{name}.{nameof(m_Ua)}", isTrainable: true);
            m_Wa = new WeightTensor(new long[2] { hiddenDim, hiddenDim }, deviceId, normal: true, name: $"{name}.{nameof(m_Wa)}", isTrainable: true);
            m_bUa = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(m_bUa)}", isTrainable: true);
            m_bWa = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(m_bWa)}", isTrainable: true);
            m_V = new WeightTensor(new long[2] { hiddenDim, 1 }, deviceId, normal: true, name: $"{name}.{nameof(m_V)}", isTrainable: true);

            if (m_enableCoverageModel)
            {
                m_Wc = new WeightTensor(new long[2] { k_coverageModelDim, hiddenDim }, deviceId, normal: true, name: $"{name}.{nameof(m_Wc)}", isTrainable: true);
                m_bWc = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(m_bWc)}", isTrainable: true);
                m_coverage = new LSTMCell(name: $"{name}.{nameof(m_coverage)}", hdim: k_coverageModelDim, dim: 1 + contextDim + hiddenDim, deviceId: deviceId);
            }
        }

        public int GetDeviceId()
        {
            return m_deviceId;
        }

        public AttentionPreProcessResult PreProcess(IWeightTensor inputs, int batchSize, IComputeGraph g)
        {
            int srcSeqLen = inputs.Rows / batchSize;

            AttentionPreProcessResult r = new AttentionPreProcessResult
            {
                rawInputs = inputs,
                inputsBatchFirst = g.TransposeBatch(inputs, batchSize)
            };

            r.uhs = g.Affine(r.inputsBatchFirst, m_Ua, m_bUa);
            r.uhs = g.View(r.uhs, batchSize, srcSeqLen, -1);


            if (m_enableCoverageModel)
            {
                m_coverage.Reset(g.GetWeightFactory(), r.inputsBatchFirst.Rows);
            }

            return r;
        }

        public IWeightTensor Perform(IWeightTensor state, AttentionPreProcessResult attenPreProcessResult, int batchSize, IComputeGraph graph)
        {
            int srcSeqLen = attenPreProcessResult.inputsBatchFirst.Rows / batchSize;

            using (IComputeGraph g = graph.CreateSubGraph(m_name))
            {
                // Affine decoder state
                IWeightTensor wc = g.Affine(state, m_Wa, m_bWa);

                // Expand dims from [batchSize x decoder_dim] to [batchSize x srcSeqLen x decoder_dim]
                IWeightTensor wc1 = g.View(wc, batchSize, 1, wc.Columns);
                IWeightTensor wcExp = g.Expand(wc1, batchSize, srcSeqLen, wc.Columns);

                IWeightTensor ggs = null;
                if (m_enableCoverageModel)
                {
                    // Get coverage model status at {t-1}
                    IWeightTensor wCoverage = g.Affine(m_coverage.Hidden, m_Wc, m_bWc);
                    IWeightTensor wCoverage1 = g.View(wCoverage, batchSize, srcSeqLen, -1);

                    ggs = g.AddTanh(attenPreProcessResult.uhs, wcExp, wCoverage1);
                }
                else
                {
                    ggs = g.AddTanh(attenPreProcessResult.uhs, wcExp);
                }

                IWeightTensor ggss = g.View(ggs, batchSize * srcSeqLen, -1);
                IWeightTensor atten = g.Mul(ggss, m_V);

                IWeightTensor attenT = g.Transpose(atten);
                IWeightTensor attenT2 = g.View(attenT, batchSize, srcSeqLen);

                IWeightTensor attenSoftmax1 = g.Softmax(attenT2, inPlace: true);

                IWeightTensor attenSoftmax = g.View(attenSoftmax1, batchSize, 1, srcSeqLen);
                IWeightTensor inputs2 = g.View(attenPreProcessResult.inputsBatchFirst, batchSize, srcSeqLen, attenPreProcessResult.inputsBatchFirst.Columns);

                IWeightTensor contexts = graph.MulBatch(attenSoftmax, inputs2, batchSize);

                if (m_enableCoverageModel)
                {
                    // Concatenate tensor as input for coverage model
                    IWeightTensor aCoverage = g.View(attenSoftmax1, attenPreProcessResult.inputsBatchFirst.Rows, 1);


                    IWeightTensor state2 = g.View(state, batchSize, 1, state.Columns);
                    IWeightTensor state3 = g.Expand(state2, batchSize, srcSeqLen, state.Columns);
                    IWeightTensor state4 = g.View(state3, batchSize * srcSeqLen, -1);


                    IWeightTensor concate = g.ConcatColumns(aCoverage, attenPreProcessResult.inputsBatchFirst, state4);
                    m_coverage.Step(concate, graph);
                }


                return contexts;
            }
        }


        public virtual List<IWeightTensor> GetParams()
        {
            List<IWeightTensor> response = new List<IWeightTensor>
            {
                m_Ua,
                m_Wa,
                m_bUa,
                m_bWa,
                m_V
            };

            if (m_enableCoverageModel)
            {
                response.Add(m_Wc);
                response.Add(m_bWc);
                response.AddRange(m_coverage.getParams());
            }

            return response;
        }

        public void Save(Stream stream)
        {
            m_Ua.Save(stream);
            m_Wa.Save(stream);
            m_bUa.Save(stream);
            m_bWa.Save(stream);
            m_V.Save(stream);

            if (m_enableCoverageModel)
            {
                m_Wc.Save(stream);
                m_bWc.Save(stream);
                m_coverage.Save(stream);
            }
        }


        public void Load(Stream stream)
        {
            m_Ua.Load(stream);
            m_Wa.Load(stream);
            m_bUa.Load(stream);
            m_bWa.Load(stream);
            m_V.Load(stream);

            if (m_enableCoverageModel)
            {
                m_Wc.Load(stream);
                m_bWc.Load(stream);
                m_coverage.Load(stream);
            }
        }

        public INeuralUnit CloneToDeviceAt(int deviceId)
        {
            AttentionUnit a = new AttentionUnit(m_name, m_hiddenDim, m_contextDim, deviceId, m_enableCoverageModel);
            return a;
        }
    }
}



