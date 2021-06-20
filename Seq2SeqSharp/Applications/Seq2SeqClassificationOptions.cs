﻿using AdvUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Seq2SeqSharp.Applications
{
    public class Seq2SeqClassificationOptions : Seq2SeqOptions
    {
        [Arg("The vocabulary file path for classification.", "ClsVocab")]
        public string ClsVocab = null;
    }
}
