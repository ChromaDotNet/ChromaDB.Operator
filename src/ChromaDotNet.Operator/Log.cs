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

	[LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "{Server}: the API answered {Status}: {Body}")]
	public static partial void ApiError(ILogger logger, Exception exception, string server, System.Net.HttpStatusCode status, string? body);
}
