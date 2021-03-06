﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;

namespace Microsoft.CodeAnalysis.Editor.Implementation.InlineRename
{
    [ExportCommandHandler(PredefinedCommandHandlerNames.Rename,
       ContentTypeNames.RoslynContentType)]
    internal partial class RenameCommandHandler
    {
        private readonly InlineRenameService _renameService;
        private readonly IEditorOperationsFactoryService _editorOperationsFactoryService;
        private readonly IWaitIndicator _waitIndicator;

        [ImportingConstructor]
        public RenameCommandHandler(
            InlineRenameService renameService,
            IEditorOperationsFactoryService editorOperationsFactoryService,
            IWaitIndicator waitIndicator)
        {
            _renameService = renameService;
            _editorOperationsFactoryService = editorOperationsFactoryService;
            _waitIndicator = waitIndicator;
        }

        private CommandState GetCommandState(Func<CommandState> nextHandler)
        {
            if (_renameService.ActiveSession != null)
            {
                return CommandState.Available;
            }

            return nextHandler();
        }

        private void HandlePossibleTypingCommand(CommandArgs args, Action nextHandler, Action<SnapshotSpan> actionIfInsideActiveSpan)
        {
            if (_renameService.ActiveSession == null)
            {
                nextHandler();
                return;
            }

            var selectedSpans = args.TextView.Selection.GetSnapshotSpansOnBuffer(args.SubjectBuffer);

            if (selectedSpans.Count > 1)
            {
                // If we have multiple spans active, then that means we have something like box
                // selection going on. In this case, we'll just forward along.
                nextHandler();
                return;
            }

            var singleSpan = selectedSpans.Single();
            SnapshotSpan containingSpan;
            if (_renameService.ActiveSession.TryGetContainingEditableSpan(singleSpan.Start, out containingSpan) &&
                containingSpan.Contains(singleSpan))
            {
                actionIfInsideActiveSpan(containingSpan);
            }
            else
            {
                var selection = args.TextView.Selection.VirtualSelectedSpans.First();

                // It's in a read-only area, so let's commit the rename and then let the character go
                // through
                _renameService.ActiveSession.Commit();

                var translatedSelection = selection.TranslateTo(args.TextView.TextBuffer.CurrentSnapshot);
                args.TextView.Selection.Select(translatedSelection.Start, translatedSelection.End);
                args.TextView.Caret.MoveTo(translatedSelection.End);

                nextHandler();
            }
        }
    }
}
