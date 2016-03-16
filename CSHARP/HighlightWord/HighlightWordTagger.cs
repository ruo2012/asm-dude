// The MIT License (MIT)
//
// Copyright (c) 2016 H.J. Lebbink
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;
using System.Globalization;
using System.Diagnostics;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Text.Classification;

namespace AsmDude.HighlightWord {


    [Export(typeof(EditorFormatDefinition))]
    [Name("AsmDude.HighlightWordFormatDefinition")]
    [UserVisible(true)]
    internal class HighlightWordFormatDefinition : MarkerFormatDefinition {
        public HighlightWordFormatDefinition() {
            this.BackgroundColor = AsmDudeToolsStatic.convertColor(Properties.Settings.Default.KeywordHighlightColor);
            this.DisplayName = "Highlight Word";
            this.ZOrder = 5;
        }
    }

    /// <summary>
    /// Derive from TextMarkerTag, in case anyone wants to consume
    /// just the HighlightWordTags by themselves.
    /// </summary>
    public class HighlightWordTag : TextMarkerTag {
        public HighlightWordTag() : base("AsmDude.HighlightWordFormatDefinition") {
            // empty
        }
    }

    /// <summary>
    /// This tagger will provide tags for every word in the buffer that
    /// matches the word currently under the cursor.
    /// </summary>
    public class HighlightWordTagger : ITagger<HighlightWordTag> {
        private ITextView _view { get; set; }
        private ITextBuffer _sourceBuffer { get; set; }
        private ITextSearchService _textSearchService { get; set; }
        private ITextStructureNavigator _textStructureNavigator { get; set; }
        private object _updateLock = new object();

        // The current set of words to highlight
        private NormalizedSnapshotSpanCollection WordSpans { get; set; }
        private SnapshotSpan? CurrentWord { get; set; }

        // The current request, from the last cursor movement or view render
        private SnapshotPoint RequestedPoint { get; set; }

        public HighlightWordTagger(ITextView view, ITextBuffer sourceBuffer, ITextSearchService textSearchService,
                                   ITextStructureNavigator textStructureNavigator) {
            _view = view;
            _sourceBuffer = sourceBuffer;
            _textSearchService = textSearchService;
            _textStructureNavigator = textStructureNavigator;

            WordSpans = new NormalizedSnapshotSpanCollection();
            CurrentWord = null;

            // Subscribe to both change events in the view - any time the view is updated
            // or the caret is moved, we refresh our list of highlighted words.
            _view.Caret.PositionChanged += CaretPositionChanged;
            _view.LayoutChanged += ViewLayoutChanged;
        }

        #region Event Handlers

        /// <summary>
        /// Force an update if the view layout changes
        /// </summary>
        private void ViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) {
            // If a new snapshot wasn't generated, then skip this layout
            if (e.NewViewState.EditSnapshot != e.OldViewState.EditSnapshot) {
                UpdateAtCaretPosition(_view.Caret.Position);
            }
        }

        /// <summary>
        /// Force an update if the caret position changes
        /// </summary>
        private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e) {
            UpdateAtCaretPosition(e.NewPosition);
        }

        /// <summary>
        /// Check the caret position. If the caret is on a new word, update the CurrentWord value
        /// </summary>
        private void UpdateAtCaretPosition(CaretPosition caretPoisition) {
            SnapshotPoint? point = caretPoisition.Point.GetPoint(_sourceBuffer, caretPoisition.Affinity);

            if (!point.HasValue) {
                return;
            }
            // If the new cursor position is still within the current word (and on the same snapshot),
            // we don't need to check it.
            if (CurrentWord.HasValue &&
                CurrentWord.Value.Snapshot == _view.TextSnapshot &&
                point.Value >= CurrentWord.Value.Start &&
                point.Value <= CurrentWord.Value.End) {
                return;
            }

            RequestedPoint = point.Value;

            ThreadPool.QueueUserWorkItem(UpdateWordAdornments);
        }

        /// <summary>
        /// The currently highlighted word has changed. Update the adornments to reflect this change
        /// </summary>
        private void UpdateWordAdornments(object threadContext) {
            SnapshotPoint currentRequest = RequestedPoint;

            List<SnapshotSpan> wordSpans = new List<SnapshotSpan>();

            // Find all words in the buffer like the one the caret is on
            try {
                TextExtent word = _textStructureNavigator.GetExtentOfWord(currentRequest);

                bool foundWord = true;
                // If we've selected something not worth highlighting, we might have
                // missed a "word" by a little bit
                if (!WordExtentIsValid(currentRequest, word)) {
                    // Before we retry, make sure it is worthwhile
                    if (word.Span.Start != currentRequest ||
                        currentRequest == currentRequest.GetContainingLine().Start ||
                        char.IsWhiteSpace((currentRequest - 1).GetChar())) {
                        foundWord = false;
                    } else {
                        // Try again, one character previous.  If the caret is at the end of a word, then
                        // this will pick up the word we are at the end of.
                        word = _textStructureNavigator.GetExtentOfWord(currentRequest - 1);

                        // If we still aren't valid the second time around, we're done
                        if (!WordExtentIsValid(currentRequest, word)) {
                            foundWord = false;
                        }
                    }
                }
                if (!foundWord) {
                    // If we couldn't find a word, just clear out the existing markers
                    SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(), null);
                    return;
                }

                SnapshotSpan currentWord = word.Span;

                // If this is the same word we currently have, we're done (e.g. caret moved within a word).
                if (CurrentWord.HasValue && currentWord == CurrentWord) {
                    return;
                }
                // Find the new spans
                FindData findData;
                string currentWordStr = currentWord.GetText();
                if (AsmDudeToolsStatic.isRegister(currentWordStr)) {
                    //Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "INFO: {0}:SynchronousUpdate. Register={1}", this.ToString(), currentWordStr));
                    findData = new FindData(AsmDudeToolsStatic.getRelatedRegister(currentWordStr), currentWord.Snapshot);
                    findData.FindOptions = FindOptions.WholeWord | FindOptions.SingleLine | FindOptions.UseRegularExpressions;
                } else {
                    //Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "INFO: {0}:SynchronousUpdate. Keyword={1}", this.ToString(), currentWordStr));
                    findData = new FindData(currentWordStr, currentWord.Snapshot);
                    findData.FindOptions = FindOptions.WholeWord;
                }

                wordSpans.AddRange(_textSearchService.FindAll(findData));

                // If we are still up-to-date (another change hasn't happened yet), do a real update
                if (currentRequest == RequestedPoint) {
                    SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(wordSpans), currentWord);
                }
            } catch (Exception e) {
                Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "ERROR: {0}:UpdateWordAdornments. Something went wrong. e={1}", this.ToString(), e.ToString()));
            }
        }

        /// <summary>
        /// Determine if a given "word" should be highlighted
        /// </summary>
        static bool WordExtentIsValid(SnapshotPoint currentRequest, TextExtent word) {
            return word.IsSignificant && currentRequest.Snapshot.GetText(word.Span).Any(c => char.IsLetter(c));
        }

        /// <summary>
        /// Perform a synchronous update, in case multiple background threads are running
        /// </summary>
        private void SynchronousUpdate(SnapshotPoint currentRequest, NormalizedSnapshotSpanCollection newSpans, SnapshotSpan? newCurrentWord) {
            lock (_updateLock) {
                if (currentRequest != RequestedPoint) {
                    return;
                }
                WordSpans = newSpans;
                CurrentWord = newCurrentWord;

                var tempEvent = TagsChanged;
                if (tempEvent != null) {
                    tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(_sourceBuffer.CurrentSnapshot, 0, _sourceBuffer.CurrentSnapshot.Length)));
                }
            }
        }

        #endregion

        #region ITagger<HighlightWordTag> Members

        /// <summary>
        /// Find every instance of CurrentWord in the given span
        /// </summary>
        /// <param name="spans">A read-only span of text to be searched for instances of CurrentWord</param>
        public IEnumerable<ITagSpan<HighlightWordTag>> GetTags(NormalizedSnapshotSpanCollection spans) {
            if (Properties.Settings.Default.KeywordHighlight_On) {
                if (CurrentWord == null) {
                    yield break;
                }
                if (spans.Count == 0 || WordSpans.Count == 0) {
                    yield break;
                }

                // Hold on to a "snapshot" of the word spans and current word, so that we maintain the same
                // collection throughout
                SnapshotSpan currentWordLocal = CurrentWord.Value;
                NormalizedSnapshotSpanCollection wordSpansLocal = WordSpans;

                // If the requested snapshot isn't the same as the one our words are on, translate our spans
                // to the expected snapshot
                if (spans[0].Snapshot != wordSpansLocal[0].Snapshot) {
                    wordSpansLocal = new NormalizedSnapshotSpanCollection(
                        wordSpansLocal.Select(span => span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive)));

                    currentWordLocal = currentWordLocal.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
                }

                //Debug.WriteLine("INFO: GetTags: currentWord=" + currentWordLocal.GetText());

                // First, yield back the word the cursor is under (if it overlaps)
                // Note that we'll yield back the same word again in the wordspans collection;
                // the duplication here is expected.
                if (spans.OverlapsWith(new NormalizedSnapshotSpanCollection(currentWordLocal))) {
                    yield return new TagSpan<HighlightWordTag>(currentWordLocal, new HighlightWordTag());
                }

                // Second, yield all the other words in the file
                foreach (SnapshotSpan span in NormalizedSnapshotSpanCollection.Overlap(spans, wordSpansLocal)) {
                    yield return new TagSpan<HighlightWordTag>(span, new HighlightWordTag());
                }
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #endregion
    }
}