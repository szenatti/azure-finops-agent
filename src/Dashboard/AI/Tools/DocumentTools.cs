using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Packages model-authored prose (Markdown or plain text) as a downloadable file.
/// Without this tool the model writes a report with a shell built-in and then invents
/// a link the browser cannot resolve, because only registered artifacts are served.
///
/// Reuses ScriptTools.GeneratedFiles + the __SCRIPT_READY__ marker so the SSE handler,
/// /api/download/script/{id} endpoint, transcript replay, frontend download card and
/// 30-min cleanup all work unchanged.
/// </summary>
public static class DocumentTools
{
    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateDocument, "GenerateDocument",
            @"Packages a Markdown (.md) or plain-text (.txt) document as a downloadable file with a download card in the UI.

Call this whenever the user asks for a report, summary, discovery write-up, notes, runbook or hand-off document ""as a file"", ""as markdown"", ""as a .md"", ""to download"", ""to share"" or ""to send to my team"".

This is the ONLY way to deliver a .md/.txt file. Writing one with bash/create_file delivers nothing to the user, and a sandbox:/file:/absolute path is never a working link.

Pass the FULL document text in documentContent — the tool does not summarize or reformat. Use GenerateScript for runnable .sh/.ps1 and GenerateHtmlPresentation for slide decks.");
    }

    private static Task<string> GenerateDocument(
        [Description(@"The complete document body. For markdown use real headings (#, ##), tables, and bullet lists. Write the whole document here — do not abbreviate or reference earlier chat text the file cannot contain.")] string documentContent,
        [Description("Filename without extension. Default: 'finops-report'")] string? filename,
        [Description("Document format: 'markdown' for .md, 'text' for .txt. Default: 'markdown'")] string? format,
        [Description("One-line description shown on the download card")] string? description)
    {
        var owner = HttpHelper.CurrentTurnUserId();
        if (!owner.HasValue) return Task.FromResult("Error: Artifact owner is unavailable.");
        if (string.IsNullOrWhiteSpace(documentContent))
            return Task.FromResult("Error: No document content provided.");

        ScriptTools.CleanupOldFiles();

        var fmt = (format ?? "markdown").Trim().ToLowerInvariant() switch
        {
            "text" or "txt" or "plain" => "text",
            _ => "markdown",
        };
        var ext = fmt == "text" ? ".txt" : ".md";
        var fileId = Guid.NewGuid().ToString("N")[..12];
        var safeName = StripExtension(TempFileHelper.SanitizeFilename(filename ?? "finops-report", "finops-report"), ext);
        var outputPath = Path.Combine(Path.GetTempPath(), $"{fileId}_{safeName}{ext}");

        // The marker is one line and colon-delimited, so the description must not
        // reintroduce either separator.
        var desc = string.IsNullOrWhiteSpace(description) ? "FinOps document" : description;
        desc = desc.ReplaceLineEndings(" ").Replace(':', '-').Trim();

        File.WriteAllText(outputPath, documentContent, new UTF8Encoding(false));

        ScriptTools.GeneratedFiles[fileId] = (outputPath, DateTime.UtcNow, documentContent, owner);

        var lineCount = documentContent.Split('\n').Length;

        return Task.FromResult($"__SCRIPT_READY__:{fileId}:{safeName}{ext}:{lineCount}:{fmt}:{desc}");
    }

    private static string StripExtension(string name, string ext) =>
        name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length
            ? name[..^ext.Length]
            : name;
}
