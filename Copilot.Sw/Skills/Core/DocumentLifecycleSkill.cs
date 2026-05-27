using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Open / save / close / activate SolidWorks documents. These are the
/// prerequisites for any other skill that operates on “the” document — if
/// the model can't open a file it can't reason about it.
/// </summary>
public sealed class DocumentLifecycleSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(OpenDocument))]
    [Description("Open an existing SolidWorks document (part, assembly, or " +
        "drawing) from disk and activate it. Returns the document's title.")]
    public string OpenDocument(
        [Description("Absolute file path to the document.")] string path)
    {
        if (Sw is null)
        {
            throw new InvalidOperationException("SolidWorks is not running.");
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Document not found.", path);
        }

        var docType = GuessDocType(path);
        int errors = 0, warnings = 0;
        var doc = Sw.OpenDoc6(
            path,
            (int)docType,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            string.Empty,
            ref errors,
            ref warnings);

        if (doc is null)
        {
            throw new InvalidOperationException(
                $"SolidWorks failed to open '{path}' (errors=0x{errors:X}, warnings=0x{warnings:X}).");
        }

        return doc.GetTitle();
    }

    [KernelFunction(nameof(SaveActiveDocument))]
    [Description("Save the active SolidWorks document to its current path.")]
    public void SaveActiveDocument()
    {
        var doc = RequireActiveDoc();
        int errors = 0, warnings = 0;
        doc.Save3(
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
            ref errors,
            ref warnings);
    }

    [KernelFunction(nameof(SaveActiveDocumentAs))]
    [Description("Save the active SolidWorks document to a new absolute path. " +
        "The file extension determines the format (.sldprt/.sldasm/.slddrw, " +
        "or any export format SolidWorks supports such as .step/.iges/.pdf).")]
    [RequiresConfirmation("Save As")]
    public void SaveActiveDocumentAs(
        [Description("Absolute target file path including extension.")] string path)
    {
        var doc = RequireActiveDoc();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        int errors = 0, warnings = 0;
        doc.Extension.SaveAs(
            path,
            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
            null,
            ref errors,
            ref warnings);

        if (errors != 0)
        {
            throw new InvalidOperationException(
                $"SaveAs failed (errors=0x{errors:X}, warnings=0x{warnings:X}).");
        }
    }

    [KernelFunction(nameof(CloseActiveDocument))]
    [Description("Close the active SolidWorks document. Pass true to save " +
        "first; otherwise unsaved changes are discarded.")]
    [RequiresConfirmation("Close document")]
    public void CloseActiveDocument(
        [Description("Save the document before closing.")] bool save = false)
    {
        if (Sw is null)
        {
            return;
        }
        var doc = ActiveSwDoc;
        if (doc is null)
        {
            return;
        }

        if (save)
        {
            int errors = 0, warnings = 0;
            doc.Save3(
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                ref errors,
                ref warnings);
        }

        Sw.CloseDoc(doc.GetTitle());
    }

    [KernelFunction(nameof(ListOpenDocuments))]
    [Description("List the titles of all currently open SolidWorks documents, " +
        "newline-separated. The active document is marked with '*'.")]
    public string ListOpenDocuments()
    {
        if (Sw is null)
        {
            return string.Empty;
        }

        var active = ActiveSwDoc?.GetTitle();
        var titles = new List<string>();
        var doc = (IModelDoc2?)Sw.GetFirstDocument();
        while (doc is not null)
        {
            var title = doc.GetTitle();
            titles.Add(string.Equals(title, active, StringComparison.Ordinal)
                ? $"* {title}"
                : $"  {title}");
            doc = (IModelDoc2?)doc.GetNext();
        }
        return string.Join(System.Environment.NewLine, titles);
    }

    [KernelFunction(nameof(ActivateDocument))]
    [Description("Bring the document with the given title to the front. " +
        "Use ListOpenDocuments to discover available titles.")]
    public void ActivateDocument(
        [Description("Document title as shown in the SolidWorks title bar.")] string title)
    {
        if (Sw is null)
        {
            throw new InvalidOperationException("SolidWorks is not running.");
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title is required.", nameof(title));
        }

        int errors = 0;
        var doc = Sw.ActivateDoc3(
            title,
            true,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
            ref errors);
        if (doc is null)
        {
            throw new InvalidOperationException(
                $"No open document titled '{title}'. " +
                "Call ListOpenDocuments first.");
        }
    }

    private IModelDoc2 RequireActiveDoc()
    {
        var doc = ActiveSwDoc;
        if (doc is null)
        {
            throw new InvalidOperationException("No active SolidWorks document.");
        }
        return doc;
    }

    private static swDocumentTypes_e GuessDocType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".sldprt" or ".prtdot" => swDocumentTypes_e.swDocPART,
            ".sldasm" or ".asmdot" => swDocumentTypes_e.swDocASSEMBLY,
            ".slddrw" or ".drwdot" => swDocumentTypes_e.swDocDRAWING,
            _ => swDocumentTypes_e.swDocPART,
        };
    }
}
