﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Editor.Undo;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.Notification;

namespace Microsoft.CodeAnalysis.Editor.Implementation.CodeActions
{
    [Export(typeof(ICodeActionEditHandlerService))]
    internal class CodeActionEditHandlerService : ICodeActionEditHandlerService
    {
        private readonly IPreviewFactoryService _previewService;
        private readonly IInlineRenameService _renameService;
        private readonly ITextBufferAssociatedViewService _associatedViewService;

        [ImportingConstructor]
        public CodeActionEditHandlerService(
            IPreviewFactoryService previewService,
            IInlineRenameService renameService,
            ITextBufferAssociatedViewService associatedViewService)
        {
            _previewService = previewService;
            _renameService = renameService;
            _associatedViewService = associatedViewService;
        }

        public ITextBufferAssociatedViewService AssociatedViewService
        {
            get { return _associatedViewService; }
        }

        public object GetPreview(Workspace workspace, IEnumerable<CodeActionOperation> operations, CancellationToken cancellationToken, DocumentId preferredDocumentId = null, ProjectId preferredProjectId = null)
        {
            var previewObjects = GetPreviews(workspace, operations, cancellationToken);
            return previewObjects == null ? null : previewObjects.TakeNextPreview(preferredDocumentId, preferredProjectId);
        }

        public SolutionPreviewResult GetPreviews(Workspace workspace, IEnumerable<CodeActionOperation> operations, CancellationToken cancellationToken)
        {
            if (operations == null)
            {
                return null;
            }

            foreach (var op in operations)
            {
                var applyChanges = op as ApplyChangesOperation;
                if (applyChanges != null)
                {
                    var oldSolution = workspace.CurrentSolution;
                    var newSolution = applyChanges.ChangedSolution.WithMergedLinkedFileChangesAsync(oldSolution, cancellationToken: cancellationToken).WaitAndGetResult(cancellationToken);
                    var preview = _previewService.GetSolutionPreviews(
                        oldSolution, newSolution, cancellationToken);

                    if (preview != null && !preview.IsEmpty)
                    {
                        return preview;
                    }
                }

                var previewOp = op as PreviewOperation;
                if (previewOp != null)
                {
                    var customPreview = previewOp.GetPreview();
                    if (customPreview != null)
                    {
                        return new SolutionPreviewResult(new List<SolutionPreviewItem>() { new SolutionPreviewItem(null, null, new Lazy<object>(() => customPreview)) });
                    }
                }

                var title = op.Title;
                if (title != null)
                {
                    return new SolutionPreviewResult(new List<SolutionPreviewItem>() { new SolutionPreviewItem(null, null, new Lazy<object>(() => title)) });
                }
            }

            return null;
        }

        public void Apply(Workspace workspace, Document fromDocument, IEnumerable<CodeActionOperation> operations, string title, CancellationToken cancellationToken)
        {
            if (_renameService.ActiveSession != null)
            {
                workspace.Services.GetService<INotificationService>()?.SendNotification(
                    EditorFeaturesResources.CannotApplyOperationWhileRenameSessionIsActive,
                    severity: NotificationSeverity.Error);
                return;
            }

#if DEBUG
            var documentErrorLookup = new HashSet<DocumentId>();
            foreach (var project in workspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (!document.HasAnyErrors(cancellationToken).WaitAndGetResult(cancellationToken))
                    {
                        documentErrorLookup.Add(document.Id);
                    }
                }
            }
#endif

            var oldSolution = workspace.CurrentSolution;
            Solution updatedSolution = oldSolution;

            foreach (var operation in operations)
            {
                var applyChanges = operation as ApplyChangesOperation;
                if (applyChanges == null)
                {
                    operation.Apply(workspace, cancellationToken);
                    continue;
                }

                // there must be only one ApplyChangesOperation, we will ignore all other ones.
                if (updatedSolution == oldSolution)
                {
                    updatedSolution = applyChanges.ChangedSolution;

                    // check whether it contains only 1 or 0 changed documents
                    if (!updatedSolution.GetChanges(oldSolution).GetProjectChanges().SelectMany(pd => pd.GetChangedDocuments()).Skip(1).Any())
                    {
                        operation.Apply(workspace, cancellationToken);
                        continue;
                    }

                    // multiple file changes
                    using (var undoTransaction = workspace.OpenGlobalUndoTransaction(title))
                    {
                        operation.Apply(workspace, cancellationToken);

                        // link current file in the global undo transaction
                        if (fromDocument != null)
                        {
                            undoTransaction.AddDocument(fromDocument.Id);
                        }

                        undoTransaction.Commit();
                    }

                    continue;
                }
            }

#if DEBUG
            foreach (var project in workspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (documentErrorLookup.Contains(document.Id))
                    {
                        document.VerifyNoErrorsAsync("CodeAction introduced error in error-free code", cancellationToken).Wait(cancellationToken);
                    }
                }
            }
#endif

            TryStartRenameSession(workspace, oldSolution, updatedSolution, cancellationToken);
        }

        private void TryStartRenameSession(Workspace workspace, Solution oldSolution, Solution newSolution, CancellationToken cancellationToken)
        {
            var changedDocuments = newSolution.GetChangedDocuments(oldSolution);
            foreach (var documentId in changedDocuments)
            {
                var document = newSolution.GetDocument(documentId);
                var root = document.GetSyntaxRootAsync(cancellationToken).WaitAndGetResult(cancellationToken);

                var renameTokenOpt = root.GetAnnotatedNodesAndTokens(RenameAnnotation.Kind)
                                         .Where(s => s.IsToken)
                                         .Select(s => s.AsToken())
                                         .FirstOrNullable();

                if (renameTokenOpt.HasValue)
                {
                    // It's possible that the workspace's current solution is not the same as
                    // newSolution. This can happen if the workspace host performs other edits
                    // during ApplyChanges, such as in the Venus scenario where indentation and
                    // formatting can happen. To work around this, we create a SyntaxPath to the
                    // rename token in the newSolution and resolve it to the current solution.

                    var pathToRenameToken = new SyntaxPath(renameTokenOpt.Value);
                    var latestDocument = workspace.CurrentSolution.GetDocument(documentId);
                    var latestRoot = latestDocument.GetSyntaxRootAsync(cancellationToken).WaitAndGetResult(cancellationToken);

                    SyntaxNodeOrToken resolvedRenameToken;
                    if (pathToRenameToken.TryResolve(latestRoot, out resolvedRenameToken) &&
                        resolvedRenameToken.IsToken)
                    {
                        var editorWorkspace = workspace;
                        var navigationService = editorWorkspace.Services.GetService<IDocumentNavigationService>();
                        if (navigationService.TryNavigateToSpan(editorWorkspace, documentId, resolvedRenameToken.Span))
                        {
                            var openDocument = workspace.CurrentSolution.GetDocument(documentId);
                            var openRoot = openDocument.GetSyntaxRootAsync(cancellationToken).WaitAndGetResult(cancellationToken);

                            // NOTE: We need to resolve the syntax path again in case VB line commit kicked in
                            // due to the navigation.

                            // TODO(DustinCa): We still have a potential problem here with VB line commit,
                            // because it can insert tokens and all sorts of other business, which could
                            // wind up with us not being able to resolve the token.
                            if (pathToRenameToken.TryResolve(openRoot, out resolvedRenameToken) &&
                                resolvedRenameToken.IsToken)
                            {
                                var snapshot = openDocument.GetTextAsync(cancellationToken).WaitAndGetResult(cancellationToken).FindCorrespondingEditorTextSnapshot();
                                if (snapshot != null)
                                {
                                    _renameService.StartInlineSession(openDocument, resolvedRenameToken.AsToken().Span, cancellationToken);
                                }
                            }
                        }
                    }

                    return;
                }
            }
        }
    }
}
