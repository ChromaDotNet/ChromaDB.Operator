using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator;

internal static partial class Log
{
	[LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Server}: creating Pod {Pod} with {Image}.")]
	public static partial void CreatingPod(ILogger logger, string server, string pod, string image);

	[LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{Server}: deleting Pod {Pod}: {Reason}")]
	public static partial void DeletingPod(ILogger logger, string server, string pod, string reason);

	[LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "{Server}: creating PersistentVolumeClaim {Pvc} of {Size}.")]
	public static partial void CreatingPvc(ILogger logger, string server, string pvc, string size);

	[LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "{Server}: deleted.")]
	public static partial void Deleted(ILogger logger, string server);

	[LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "{Server}: the API refused the image it admitted on Pod {Pod}, which the next pass will take for an edit: {Body}")]
	public static partial void AdmittedImageRefused(ILogger logger, string server, string pod, string? body);

	[LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "{Server}: the pass failed ({Reason}); the next one in {Seconds} seconds: {Message}")]
	public static partial void PassFailed(ILogger logger, Exception exception, string server, string reason, double seconds, string message);

	[LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "{Server}: deleted with pvcDeletionPolicy Retain: PersistentVolumeClaim {Pvc} no longer has it as owner, and stays.")]
	public static partial void ClaimReleased(ILogger logger, string server, string pvc);

	[LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "{Server}: deleted, but the operator could not remove it from the owners of its claim; it stays and the operator tries again in {Seconds} seconds: {Message}")]
	public static partial void ClaimNotReleased(ILogger logger, Exception? exception, string server, double seconds, string message);

	[LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "{Server}: created {Kind} {Name}.")]
	public static partial void Created(ILogger logger, string server, string kind, string name);

	[LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "{Server}: creating the migration Job {Job}.")]
	public static partial void CreatingMigrationJob(ILogger logger, string server, string job);

	[LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "{Server}: deleting Pod {Pod} ({Reason}): {Message}")]
	public static partial void Repairing(ILogger logger, string server, string pod, string reason, string message);

	[LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "{Server}: deleted, but its MemberLists in {Namespace} could not be deleted: delete them by hand if no distributed Chroma comes back there.")]
	public static partial void MemberListsNotDeleted(ILogger logger, Exception exception, string server, string @namespace);

	[LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "{Server}: deleted with pvcDeletionPolicy Delete before a pass made it the owner of PersistentVolumeClaim {Pvc}: the claim stays, with the data. Delete it by hand if it is not needed.")]
	public static partial void DeleteNotApplied(ILogger logger, string server, string pvc);
}
