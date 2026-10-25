using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace ChromaDotNet.Operator.Distributed;

/// <summary>The outcome of a check: the reason and the message go into the condition PostgresReady or ObjectStorageReady.</summary>
public sealed record CheckResult(bool Ok, string Reason, string Message);

/// <param name="Namespace">The namespace of the Chroma, whose Pods resolve the names: a name of one label is a Service there.</param>
public sealed record PostgresTarget(string Namespace, string Host, int Port, string Database, string SslMode, string Username, string Password);

/// <param name="Namespace">The namespace of the Chroma, whose Pods resolve the names.</param>
public sealed record BucketTarget(string Namespace, string? Endpoint, string Bucket, string Region, string AccessKeyId, string SecretAccessKey);

/// <summary>
/// The external Postgres and bucket of a distributed Chroma, checked before anything starts (OPERATOR-DESIGN.md §7.3): without them
/// nothing works, and the components would only fail one by one, with errors far from the cause.
/// </summary>
public interface IDependencyChecks
{
	Task<CheckResult> PostgresAsync(PostgresTarget target, CancellationToken cancellationToken);

	Task<CheckResult> BucketAsync(BucketTarget target, CancellationToken cancellationToken);
}

public sealed class DependencyChecks : IDependencyChecks
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	/// <summary>A login and a query, as the sysdb and the migration Job do, with the sslmode of libpq.</summary>
	public async Task<CheckResult> PostgresAsync(PostgresTarget target, CancellationToken cancellationToken)
	{
		var builder = new NpgsqlConnectionStringBuilder
		{
			Host = await ResolvableAsync(target.Host, target.Namespace, cancellationToken),
			Port = target.Port,
			Database = target.Database,
			Username = target.Username,
			Password = target.Password,
			SslMode = target.SslMode switch
			{
				"allow" => SslMode.Allow,
				"prefer" => SslMode.Prefer,
				"require" => SslMode.Require,
				"verify-ca" => SslMode.VerifyCA,
				"verify-full" => SslMode.VerifyFull,
				_ => SslMode.Disable,
			},
			Timeout = (int)Timeout.TotalSeconds,
			CommandTimeout = (int)Timeout.TotalSeconds,
			Pooling = false,
		};
		var where = $"{target.Host}:{target.Port}/{target.Database}";
		try
		{
			await using var connection = new NpgsqlConnection(builder.ConnectionString);
			await connection.OpenAsync(cancellationToken);
			await using var command = new NpgsqlCommand("SELECT 1", connection);
			await command.ExecuteScalarAsync(cancellationToken);
			return new CheckResult(true, "Connected", $"Postgres answers on {where}.");
		}
		catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
		{
			return new CheckResult(false, "AuthenticationFailed", $"Postgres on {where} refused the login of {target.Username}: {e.MessageText}");
		}
		catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
		{
			return new CheckResult(false, "DatabaseNotFound", $"Postgres on {target.Host}:{target.Port} has no database {target.Database}: create it, the migration Job creates the tables in it.");
		}
		catch (PostgresException e)
		{
			return new CheckResult(false, "Refused", $"Postgres on {where} answered {e.SqlState}: {e.MessageText}");
		}
		catch (Exception e) when (e is NpgsqlException or SocketException or TimeoutException && !cancellationToken.IsCancellationRequested)
		{
			return new CheckResult(false, "Unreachable", $"Postgres on {where} does not answer: {Innermost(e).Message}");
		}
	}

	/// <summary>
	/// The bucket as the services of Chroma reach it. Their S3 client, in the AWS mode, uses path style only with an endpoint that is an
	/// IP; with a name it asks for &lt;bucket&gt;.&lt;host&gt; (measured on 25/10 with aws-sdk-s3 1.119.0 of Chroma 52ce844): that name must resolve.
	/// </summary>
	public async Task<CheckResult> BucketAsync(BucketTarget target, CancellationToken cancellationToken)
	{
		Uri? endpoint = target.Endpoint is null ? null : new Uri(target.Endpoint);
		var pathStyle = endpoint is not null && IPAddress.TryParse(endpoint.Host, out _);
		if (endpoint is not null && !pathStyle)
		{
			var host = $"{target.Bucket}.{endpoint.Host}";
			if (!await ResolvesAsync($"{host}.{target.Namespace}.svc", cancellationToken) && !await ResolvesAsync(host, cancellationToken))
			{
				return new CheckResult(false, "VirtualHostNotResolvable",
					$"The services of Chroma reach the bucket as {host}: with an endpoint by name their S3 client addresses the bucket in the host name, and {host} does not resolve. " +
					$"Write the endpoint with the IP of the service, like {endpoint.Scheme}://10.0.0.10:{endpoint.Port}, for which they use the path, or make {host} resolve.");
			}
		}
		var config = new AmazonS3Config
		{
			ForcePathStyle = pathStyle,
			Timeout = Timeout,
			MaxErrorRetry = 0,
			AuthenticationRegion = target.Region,
		};
		if (endpoint is null)
			config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(target.Region);
		else
			config.ServiceURL = new UriBuilder(endpoint) { Host = pathStyle ? endpoint.Host : await ResolvableAsync(endpoint.Host, target.Namespace, cancellationToken) }.Uri.ToString().TrimEnd('/');
		var where = endpoint is null ? $"bucket {target.Bucket} of AWS S3 in {target.Region}" : $"bucket {target.Bucket} on {target.Endpoint}";
		try
		{
			using var s3 = new AmazonS3Client(new BasicAWSCredentials(target.AccessKeyId, target.SecretAccessKey), config);
			await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = target.Bucket, MaxKeys = 1 }, cancellationToken);
			return new CheckResult(true, "Reachable", $"The {where} answers.");
		}
		catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
		{
			return new CheckResult(false, "BucketNotFound", $"There is no {where}: create it; the operator never creates or deletes it.");
		}
		catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
		{
			return new CheckResult(false, "AccessDenied", $"The {where} refused the keys of the Secret: {e.ErrorCode} {e.Message}");
		}
		catch (AmazonS3Exception e)
		{
			return new CheckResult(false, "Refused", $"The {where} answered {(int)e.StatusCode} {e.ErrorCode}: {e.Message}");
		}
		catch (Exception e) when (e is AmazonServiceException or HttpRequestException or SocketException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
		{
			return new CheckResult(false, "Unreachable", $"The {where} does not answer: {Innermost(e).Message}");
		}
	}

	/// <summary>
	/// The name as the Pods of the Chroma resolve it: they search their namespace first, the operator searches its own. A name the
	/// namespace of the Chroma resolves is written out with it, as &lt;name&gt;.&lt;namespace&gt;.svc; another stays as it is.
	/// </summary>
	private static async Task<string> ResolvableAsync(string host, string ns, CancellationToken cancellationToken) =>
		IPAddress.TryParse(host, out _) || !await ResolvesAsync($"{host}.{ns}.svc", cancellationToken) ? host : $"{host}.{ns}.svc";

	private static async Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken)
	{
		try
		{
			return (await Dns.GetHostAddressesAsync(host, cancellationToken)).Length > 0;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	private static Exception Innermost(Exception e)
	{
		while (e.InnerException is not null)
			e = e.InnerException;
		return e;
	}
}
