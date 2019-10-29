﻿using AdvUtils;
using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TensorSharp;
using TensorSharp.CUDA;

namespace Seq2SeqSharp
{

    public class Optimizer
    {
        public static float decay_rate = 0.999f;
        public static float smooth_eps = 1e-10f;

        public void UpdateWeights(List<IWeightTensor> model, int batchSize, float step_size, float regc, float clipval)
        {
            Dictionary<int, List<IWeightTensor>> id2Models = new Dictionary<int, List<IWeightTensor>>();
            foreach (var item in model)
            {
                if (id2Models.ContainsKey(item.DeviceId) == false)
                {
                    id2Models.Add(item.DeviceId, new List<IWeightTensor>());
                }
                id2Models[item.DeviceId].Add(item);
            }

            Parallel.ForEach(id2Models, kv => 
            {
                foreach (var item in kv.Value)
                {
                    var m = item as WeightTensor;
                    UpdateWeightsTensor(m, batchSize, step_size, clipval, regc);
                }
            });
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateWeightsTensor(WeightTensor m, int batchSize, float step_size, float clipval, float regc)
        {
            Ops.RMSProp(m.TWeight, m.TGradient, m.TCache, batchSize, step_size, clipval, regc, decay_rate, smooth_eps);
        }

       
        public void CleanCache(List<IWeightTensor> model)
        {
            foreach (var k in model)
            {
                k.CleanCache();
            }
        }
    }
}
