using System.Text.RegularExpressions;
using Dochub.Api.Contracts;
using Dochub.Api.Domain;

namespace Dochub.Api.Endpoints;

public static partial class Mapping
{
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonSlug();

    public static string Slugify(string value)
    {
        var slug = NonSlug().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "item" : slug[..Math.Min(slug.Length, 120)];
    }

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length == 0 ? "OR" : initials;
    }

    /// <summary>Human label for a source, matching the chips in the UI.</summary>
    public static string Label(this SourceType source) => source switch
    {
        SourceType.GitHub => "GitHub",
        SourceType.SharePoint => "SharePoint",
        SourceType.GoogleDrive => "Google Drive",
        SourceType.Local => "Local files",
        SourceType.AzureDevOps => "Azure DevOps",
        SourceType.Confluence => "Confluence",
        SourceType.Jira => "Jira",
        _ => source.ToString()
    };

    /// <summary>How a single file's state is written in the document list.</summary>
    public static string ToUiStatus(this DocumentStatus status) => status switch
    {
        DocumentStatus.Indexed => "Indexed",
        DocumentStatus.Processing => "Processing",
        DocumentStatus.Uploading => "Uploading",
        DocumentStatus.Uploaded => "Uploaded",
        DocumentStatus.Failed => "Failed",
        DocumentStatus.Skipped => "Skipped",
        _ => "Pending"
    };

    public static SourceType ParseSource(string value) =>
        Enum.TryParse<SourceType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : value.ToLowerInvariant() switch
            {
                "gdrive" or "googledrive" or "google-drive" => SourceType.GoogleDrive,
                "azdo" or "azuredevops" or "azure-devops" => SourceType.AzureDevOps,
                "sharepoint" or "ms365" or "microsoft365" => SourceType.SharePoint,
                _ => throw new ArgumentException($"Unknown source type '{value}'.")
            };

    public static ArtifactSummaryDto ToSummary(Artifact artifact, Domain.Group group, Team team, IReadOnlyCollection<UploadedDocument> documents)
    {
        int Count(Func<DocumentStatus, bool> predicate) => documents.Count(d => predicate(d.Status));

        var indexed = Count(s => s == DocumentStatus.Indexed);
        var processing = Count(s => s == DocumentStatus.Processing);
        var failed = Count(s => s == DocumentStatus.Failed);
        var pending = documents.Count - indexed - processing - failed - Count(s => s == DocumentStatus.Skipped);

        var status = documents.Count == 0 ? "Empty"
            : processing > 0 ? "Processing"
            : failed > 0 ? "Failed"
            : pending > 0 ? "Pending"
            : "Indexed";

        return new ArtifactSummaryDto(
            artifact.Id, artifact.Name, artifact.Slug, artifact.Category, artifact.PrimarySource.ToString(),
            group.Id, group.Name, team.Id, team.Name,
            documents.Count, indexed, pending, processing, failed, status,
            artifact.Status.ToString(), artifact.LastProcessedAt);
    }

    public static DocumentDto ToDto(this UploadedDocument d) => new(
        d.Id, d.SourceDocumentId, d.Name, d.RelativePath, d.SourceLocation, d.SourceType.ToString(),
        d.Status.ToUiStatus(), d.SizeBytes, d.ContentType, d.BlobPath, d.BlobUrl,
        d.BlobUploadedAt, d.Error, d.Revision, d.LastSyncedAt, d.CreatedAt, d.UpdatedAt,
        d.ContentMd5, d.ChunkCount, d.ProcessedAt);

    public static SourceDocumentDto ToDto(this SourceDocument s, string artifactName, string path, string requestedBy) => new(
        s.Id, s.Reference, s.ArtifactId, artifactName, path,
        s.SourceType.ToString(), s.SourceReference, s.Status.ToString(), s.Status.ToLabel(),
        s.TotalDocuments, s.UploadedDocumentCount, s.ProcessedDocumentCount, s.FailedDocumentCount,
        s.ProgressPercent(), s.BlobContainer, s.BlobPrefix, requestedBy, s.SyncScheduleId is not null,
        s.RequestedAt, s.UploadStartedAt, s.UploadedAt, s.ProcessingStartedAt, s.ProcessedAt, s.Error);

    /// <summary>
    /// Progress across the whole journey, not just one leg: uploading fills the
    /// first half, processing the second, so the bar never stalls or restarts.
    /// </summary>
    public static int ProgressPercent(this SourceDocument s)
    {
        if (s.Status is SourceDocumentStatus.Processed) return 100;
        if (s.Status is SourceDocumentStatus.Failed or SourceDocumentStatus.Cancelled) return 0;
        if (s.Status is SourceDocumentStatus.RequestUpload) return 0;
        if (s.TotalDocuments == 0) return s.Status == SourceDocumentStatus.Uploading ? 10 : 50;

        var uploaded = Math.Clamp(s.UploadedDocumentCount / (double)s.TotalDocuments, 0, 1);
        var processed = Math.Clamp(s.ProcessedDocumentCount / (double)s.TotalDocuments, 0, 1);
        return (int)Math.Round((uploaded * 0.5 + processed * 0.5) * 100);
    }

    /// <summary>The status as the UI writes it, e.g. "Request Upload".</summary>
    public static string ToLabel(this SourceDocumentStatus status) => status switch
    {
        SourceDocumentStatus.RequestUpload => "Request Upload",
        SourceDocumentStatus.Uploading => "Uploading",
        SourceDocumentStatus.Uploaded => "Uploaded",
        SourceDocumentStatus.Processing => "Processing",
        SourceDocumentStatus.Processed => "Processed",
        SourceDocumentStatus.PartiallyFailed => "Completed with errors",
        SourceDocumentStatus.Failed => "Failed",
        SourceDocumentStatus.Removing => "Removing",
        _ => "Cancelled"
    };

    public static NotificationDto ToDto(this Notification n) => new(
        n.Id, n.Type, n.Title, n.Body, n.Severity.ToString(), n.EntityType, n.EntityId,
        n.ReadAt is not null, n.CreatedAt);
}
