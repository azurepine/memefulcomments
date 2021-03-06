﻿using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace MemefulComments
{
   [Export(typeof(ILineTransformSourceProvider))]
   [ContentType(ContentTypes.Cpp), 
    ContentType(ContentTypes.CSharp), 
    ContentType(ContentTypes.VisualBasic), 
    ContentType(ContentTypes.FSharp), 
    ContentType(ContentTypes.JavaScript),
    ContentType(ContentTypes.TypeScript)]
   [TextViewRole(PredefinedTextViewRoles.Document)]
   internal class CommentLineTransformSourceProvider : ILineTransformSourceProvider
   {
      [Import]
      public ITextDocumentFactoryService TextDocumentFactory { get; set; }

      ILineTransformSource ILineTransformSourceProvider.Create(IWpfTextView view)
      {
         var manager = view.Properties.GetOrCreateSingletonProperty(() => new CommentsAdornment(view, TextDocumentFactory));
         return new CommentLineTransformSource(manager);
      }
   }
}
