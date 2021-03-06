﻿// The MIT License (MIT)
//
// Copyright (c) 2017 Henk-Jan Lebbink
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using AsmDude.SyntaxHighlighting;
using AsmDude.Tools;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;

namespace AsmDude.QuickInfo
{
    /// <summary>
    /// Factory for quick info sources
    /// </summary>
    [Export(typeof(IQuickInfoSourceProvider))]
    [ContentType(AsmDudePackage.AsmDudeContentType)]
    //[ContentType("code")] // use contenttype "code" to use quickinfo controller in disassembly window
    [TextViewRole(PredefinedTextViewRoles.Debuggable)]
    [Name("asmQuickInfo")]
    internal sealed class AsmQuickInfoSourceProvider : IQuickInfoSourceProvider
    {
        [Import]
        private IBufferTagAggregatorFactoryService _aggregatorFactory = null;

        [Import]
        private ITextDocumentFactoryService _docFactory = null;

        [Import]
        private IContentTypeRegistryService _contentService = null;

        public IQuickInfoSource TryCreateQuickInfoSource(ITextBuffer buffer)
        {
            Func<AsmQuickInfoSource> sc = delegate () {
                ITagAggregator<AsmTokenTag> aggregator = AsmDudeToolsStatic.Get_Aggregator(buffer, this._aggregatorFactory);
                ILabelGraph labelGraph = AsmDudeToolsStatic.Get_Label_Graph(buffer, this._aggregatorFactory, this._docFactory, this._contentService);
                AsmSimulator asmSimulator = AsmSimulator.GetOrCreate_AsmSimulator(buffer, this._aggregatorFactory);
                return new AsmQuickInfoSource(buffer, aggregator, labelGraph, asmSimulator);
            };
            return buffer.Properties.GetOrCreateSingletonProperty(sc);
        }
    }
}
