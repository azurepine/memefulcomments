﻿using System;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using System.Windows;
using System.Net;
using System.IO;

namespace MemefulComments
{
   /// <summary>
   /// CommentsAdornment places red boxes behind all the "a"s in the editor window
   /// </summary>
   internal sealed class CommentsAdornment : ITagger<ErrorTag>, IDisposable
   {
      /// <summary>
      /// Initializes a new instance of the <see cref="CommentsAdornment"/> class.
      /// </summary>
      /// <param name="view">Text view to create the adornment for</param>
      static CommentsAdornment()
      {
         Enabled = true;
      }

      public static void ToggleEnabled()
      {
         Enabled = !Enabled;
         string message = string.Format("Memeful comments {0}. Scroll editor window(s) to update.",
             Enabled ? "enabled" : "disabled");
         UIMessage.Show(message);
      }

      public static bool Enabled { get; set; }

      public ThreadSafeDictionary<int, CommentImage> Images { get; set; }

      private IAdornmentLayer _layer;
      private IWpfTextView _view;
      private ITextDocumentFactoryService _textDocumentFactory;
      private string _contentTypeName;
      private bool _initialised1 = false;
      private bool _initialised2 = false;
      private List<ITagSpan<ErrorTag>> _errorTags;

      private List<string> _processingUris = new List<string>();
      private ThreadSafeDictionary<WebClient, ImageParameters> _toaddImages = new ThreadSafeDictionary<WebClient, ImageParameters>();
      private ThreadSafeDictionary<int, ITextViewLine> _editedLines = new ThreadSafeDictionary<int, ITextViewLine>();

      private System.Timers.Timer _timer = new System.Timers.Timer(200);
      private object _locker = new object();

      private class ImageParameters
      {
         public string Uri { get; set; }
         public string LocalPath { get; set; }
         public CommentImage Image { get; set; }
         public ITextViewLine Line { get; set; }
         public int LineNumber { get; set; }
         public SnapshotSpan Span { get; set; }
         public double Scale { get; set; }
         public string Filepath { get; set; }
      }

      public CommentsAdornment(IWpfTextView view, ITextDocumentFactoryService textDocumentFactory)
      {
         _textDocumentFactory = textDocumentFactory;
         _view = view;
         _layer = view.GetAdornmentLayer("CommentImageAdornmentLayer");
         Images = new ThreadSafeDictionary<int, CommentImage>();
         _view.LayoutChanged += OnLayoutChanged;

         _contentTypeName = view.TextBuffer.ContentType.TypeName;
         _view.TextBuffer.ContentTypeChanged += OnContentTypeChanged;

         _errorTags = new List<ITagSpan<ErrorTag>>();

         _timer.Elapsed += _timer_Elapsed;
      }

      private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
      {
         _contentTypeName = e.AfterContentType.TypeName;
      }

      internal void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
      {
         if (!Enabled)
            return;

         _errorTags.Clear();
         TagsChanged?.Invoke(
            this, 
            new SnapshotSpanEventArgs(
               new SnapshotSpan(
                  _view.TextSnapshot, 
                  new Span(0, _view.TextSnapshot.Length))));

         lock (_locker)
         {
            foreach (ITextViewLine line in e.NewOrReformattedLines)
            {
               int lineNumber = line.Snapshot.GetLineFromPosition(line.Start.Position).LineNumber;

               _editedLines[lineNumber] = line;
            }
         }

         ResetTimer();

         // Sometimes, on loading a file in an editor view, the line transform gets triggered before the image adornments 
         // have been added, so the lines don't resize to the image height. So here's a workaround:
         // Changing the zoom level triggers the required update.
         // Need to do it twice - once to trigger the event, and again to change it back to the user's expected level.
         if (!_initialised1)
         {
            _view.ZoomLevel++;
            _initialised1 = true;
         }
         if (!_initialised2)
         {
            _view.ZoomLevel--;
            _initialised2 = true;
         }
      }

      private void ResetTimer()
      {
         _timer.Stop();
         _timer.Start();
      }

      private void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
      {
         _timer.Stop();

         string filepath = null;
         if (_textDocumentFactory != null &&
            _textDocumentFactory.TryGetTextDocument(_view.TextBuffer, out ITextDocument textDocument))
         {
            filepath = textDocument.FilePath;
         }

         lock (_locker)
         {
            foreach (var kvp in _editedLines)
            {
               try
               {
                  Application.Current.Dispatcher.Invoke(
                     () =>
                     CreateVisuals(kvp.Value, kvp.Key, filepath));
               }
               catch (InvalidOperationException ex)
               {
                  ExceptionHandler.Notify(ex, true);
               }
            }

            _editedLines.Clear();
         }
      }

      private void CreateVisuals(ITextViewLine line, int lineNumber, string filepath)
      {
         Trace.WriteLine($"VISUALS: {lineNumber}");
         var lineText = line.Extent.GetText();
         var matchIndex = CommentImageParser.Match(_contentTypeName, lineText, out string matchedText);
         if (matchIndex >= 0)
         {
            // Get coordinates of text
            var start = line.Extent.Start.Position + matchIndex;
            var end = line.Start + (line.Extent.Length - 1);
            var span = new SnapshotSpan(_view.TextSnapshot, Span.FromBounds(start, end));

            CommentImageParser.TryParse(
               matchedText, 
               out string imageUrl, out double scale, out Exception xmlParseException);

            if (xmlParseException != null)
            {
               if (Images.ContainsKey(lineNumber))
               {
                  _layer.RemoveAdornment(Images[lineNumber]);
                  Images.Remove(lineNumber);
               }

               _errorTags.Add(
                  new TagSpan<ErrorTag>(
                     span, 
                     new ErrorTag("XML parse error", GetErrorMessage(xmlParseException))));

               return;
            }

            // Check for and update existing image
            CommentImage image = Images.ContainsKey(lineNumber) ? Images[lineNumber] : null;
            var reload = false;
            if (image != null)
            {
               if (image.OriginalUrl == imageUrl && image.Scale != scale)
               {
                  // URL same but scale changed
                  image.Scale = scale;
                  reload = true;
               }
               else if (image.OriginalUrl != imageUrl)
               {
                  // URL different, must load from new source
                  reload = true;
               }
            }
            else 
            {
               // no existing image, so create new one
               image = new CommentImage();
               Images.Add(lineNumber, image);

               reload = true;
            }

            var originalUrl = imageUrl;
            if (reload)
            {
               if (_processingUris.Contains(imageUrl)) return;

               if (imageUrl.ToLower().StartsWith("http"))
               {
                  if (ImageCache.Instance.TryGetValue(imageUrl, out string localPath))
                  {
                     imageUrl = localPath;
                  }
                  else
                  {
                     _processingUris.Add(imageUrl);
                     var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(imageUrl));
                     WebClient client = new WebClient();
                     client.DownloadDataCompleted += Client_DownloadDataCompleted;

                     _toaddImages.Add(
                        client,
                        new ImageParameters()
                        {
                           Uri = imageUrl,
                           LocalPath = tempPath,
                           Image = image,
                           Line = line,
                           LineNumber = lineNumber,
                           Span = span,
                           Scale = scale,
                           Filepath = filepath
                        });

                     client.DownloadDataAsync(new Uri(imageUrl));

                     return;
                  }
               }
            }

            if (imageUrl.ToLower().StartsWith("http"))
            {
               if (ImageCache.Instance.TryGetValue(imageUrl, out string localPath))
               {
                  imageUrl = localPath;
               }
            }
            ProcessImage(image, imageUrl, originalUrl, line, lineNumber, span, scale, filepath);
         }
         else
         {
            if (Images.ContainsKey(lineNumber))
               Images.Remove(lineNumber);
         }
      }

      private void Client_DownloadDataCompleted(object sender, DownloadDataCompletedEventArgs e)
      {
         var client = sender as WebClient;

         client.DownloadDataCompleted -= Client_DownloadDataCompleted;

         if (_toaddImages.TryGetValue(client, out ImageParameters item))
         {
            byte[] data = e.Result;
            File.WriteAllBytes(item.LocalPath, data);
            ImageCache.Instance.Add(item.Uri, item.LocalPath);
            _processingUris.Remove(item.Uri);

            ProcessImage(item.Image, 
               item.LocalPath, 
               item.Uri, 
               item.Line, 
               item.LineNumber, 
               item.Span, 
               item.Scale,
               item.Filepath);

            _toaddImages.Remove(client);
         }
      }

      private void ProcessImage(CommentImage image, string imageUrl, string originalUrl, ITextViewLine line, int lineNumber, SnapshotSpan span, double scale, string filepath)
      {
         var result = image.TrySet(
            imageUrl,
            originalUrl,
            scale,
            filepath,
            out Exception imageLoadingException);

         // Position image and add as adornment
         if (imageLoadingException == null)
         {
            AddAdorment(image, line, lineNumber, span);
         }
         else
         {
            if (Images.ContainsKey(lineNumber))
               Images.Remove(lineNumber);

            _errorTags.Add(
               new TagSpan<ErrorTag>(
                  span,
                  new ErrorTag("Trouble loading image", GetErrorMessage(imageLoadingException))));
         }
      }

      private void AddAdorment(UIElement element, ITextViewLine line, int lineNumber, SnapshotSpan span)
      {
         Geometry geometry = null;
         try
         {
            geometry = _view.TextViewLines.GetMarkerGeometry(span);
         }
         catch { }

         if (geometry == null)
         {
            // Exceptional case when image dimensions are massive (e.g. specifying very large scale factor)
            throw new InvalidOperationException("Couldn't get source code line geometry. Is the loaded image massive?");
         }

         try
         {
            Canvas.SetLeft(element, geometry.Bounds.Left);
            Canvas.SetTop(element, line.TextBottom);
         } catch { }

         // Add element to the editor view
         try
         {
            _layer.RemoveAdornment(element);
            _layer.AddAdornment(
               AdornmentPositioningBehavior.TextRelative, 
               line.Extent, 
               null, 
               element, 
               null);
         }
         catch (Exception ex)
         {
            // No expected exceptions, so tell user something is wrong.
            ExceptionHandler.Notify(ex, true);
         }
      }

      private string GetErrorMessage(Exception exception)
      {
         Trace.WriteLine("Problem parsing comment text or loading image...\n" + exception);

         string message;
         if (exception is XmlException)
            message = "Problem with comment format: " + exception.Message;
         else if (exception is NotSupportedException)
            message = exception.Message + "\nThis problem could be caused by a corrupt, invalid or unsupported image file.";
         else
            message = exception.Message;
         return message;
      }

      private void UnsubscribeFromViewerEvents()
      {
         _view.LayoutChanged -= OnLayoutChanged;
         _view.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
      }

      #region ITagger<ErrorTag> Members

      public IEnumerable<ITagSpan<ErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
      {
         return _errorTags;
      }

      public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

      #region IDisposable Support
      private bool disposedValue = false; // To detect redundant calls

      void Dispose(bool disposing)
      {
         if (!disposedValue)
         {
            if (disposing)
            {
               UnsubscribeFromViewerEvents();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
            // TODO: set large fields to null.

            disposedValue = true;
         }
      }

      // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
      // ~CommentsAdornment() {
      //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      //   Dispose(false);
      // }

      // This code added to correctly implement the disposable pattern.
      public void Dispose()
      {
         // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
         Dispose(true);
         // TODO: uncomment the following line if the finalizer is overridden above.
         // GC.SuppressFinalize(this);
      }
      #endregion

      #endregion
   }
}
