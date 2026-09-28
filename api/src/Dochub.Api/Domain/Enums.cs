namespace Dochub.Api.Domain;

/// <summary>Level a user holds inside an organization. Only <see cref="Owner"/> may add members.</summary>
public enum OrgRole { Member = 0, Admin = 1, Owner = 2 }

public enum TeamRole { Member = 0, Lead = 1 }

/// <summary>Where documents originate. Drives which connector/extractor runs.</summary>
public enum SourceType
{
    Local = 0,
    GitHub = 1,
    SharePoint = 2,
    GoogleDrive = 3,
    AzureDevOps = 4,
    Confluence = 5,
    Jira = 6
}

public enum ConnectionStatus { Disconnected = 0, Connected = 1, Expired = 2, Revoked = 3 }

/// <summary>
/// Lifecycle of one source submitted for processing. Nothing exists until the
/// Process button is pressed; from there the record carries the whole journey:
///
///   RequestUpload -> Uploading -> Uploaded -> Processing -> Processed
///
/// RequestUpload  the row is written and the source is on the extract queue
/// Uploading      the extractor claimed it and is moving files to blob storage
/// Uploaded       every file is in blob storage and the process queue has the batch
/// Processing     the vector service claimed it
/// Processed      vectors are stored and the documents are searchable
/// </summary>
public enum SourceDocumentStatus
{
    RequestUpload = 0,
    Uploading = 1,
    Uploaded = 2,
    Processing = 3,
    Processed = 4,
    PartiallyFailed = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>
/// Lifecycle of a single file inside a source. Set by the extractor as it moves
/// bytes, then by the vector service as it indexes them.
/// </summary>
public enum DocumentStatus
{
    Pending = 0,
    Uploading = 1,
    Uploaded = 2,
    Processing = 3,
    Indexed = 4,
    Failed = 5,
    Skipped = 6
}

/// <summary>State of a message on a queue, for the database-backed queue used when no broker is configured.</summary>
public enum QueueMessageStatus { Pending = 0, InFlight = 1, Completed = 2, DeadLettered = 3 }

public enum NotificationSeverity { Info = 0, Success = 1, Warning = 2, Error = 3 }


/// <summary>How often a recurring source sync runs.</summary>
public enum SyncFrequency { Daily = 0, Weekly = 1, Monthly = 2 }

public enum SyncScheduleStatus { Active = 0, Paused = 1, Failed = 2 }

/// <summary>What a sync did to one document, matched against the artifact by file name.</summary>
public enum SyncAction { Unchanged = 0, Updated = 1, Added = 2, Failed = 3 }
