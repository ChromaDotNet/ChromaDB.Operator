namespace ChromaDotNet.Operator;

/// <summary>The API group, and the labels and annotations the operator puts on what it creates.</summary>
internal static class Labels
{
	public const string Group = "chroma.chromadotnet.org";

	/// <summary>The Chroma a resource belongs to.</summary>
	public const string Chroma = Group + "/chroma";

	/// <summary>The hash of the configuration in the ConfigMap, on the Pod: a change restarts the Pod.</summary>
	public const string ConfigHash = Group + "/config-hash";

	/// <summary>The hash of the Pod the operator wanted, on the Pod: any difference with a new one recreates the Pod.</summary>
	public const string PodSpecHash = Group + "/pod-spec-hash";

	/// <summary>On the Service, the keys of the annotations that come from spec.service.annotations, to remove those no longer there.</summary>
	public const string ManagedAnnotations = Group + "/managed-annotations";

	/// <summary>On a Chroma, "true" allows an image older than status.dataVersion.</summary>
	public const string AllowDowngrade = Group + "/allow-downgrade";

	/// <summary>On a Chroma, "true" allows Chroma on data that VecturaDB opened last (<see cref="Passages"/>).</summary>
	public const string AllowProductChange = Group + "/allow-product-change";

	/// <summary>
	/// On a Chroma, the version an administrator says the data has, when an image without a version ran on it, with the code of the
	/// incident it resolves: &lt;x.y.z&gt;/&lt;code&gt;, the code being status.dataVersionUnknownIncident.
	/// </summary>
	public const string DeclaredDataVersion = Group + "/declared-data-version";

	/// <summary>On the claim, the data version the operator recorded.</summary>
	public const string ClaimDataVersion = Group + "/data-version";

	/// <summary>On the claim, the product of the data version: chroma or vecturadb; a claim without it is of Chroma.</summary>
	public const string ClaimDataProduct = Group + "/data-product";

	/// <summary>On the claim, once both products have opened the data, the highest version of each: chroma=1.5.9,vecturadb=1.0.0-preview.1.</summary>
	public const string ClaimDataVersions = Group + "/data-versions";

	/// <summary>On the claim, the code of the incident when an image without a version ran on the data.</summary>
	public const string ClaimDataVersionUnknown = Group + "/data-version-unknown";

	/// <summary>
	/// On the Pod, the image the API admitted at its creation, when a mutating webhook rewrote the one the operator asked for: a Pod that
	/// runs it is the one the operator created.
	/// </summary>
	public const string AdmittedImage = Group + "/admitted-image";

	public const string ManagedBy = "chroma-operator";

	public static Dictionary<string, string> For(string server) => new()
	{
		["app.kubernetes.io/name"] = "chroma",
		["app.kubernetes.io/instance"] = server,
		["app.kubernetes.io/managed-by"] = ManagedBy,
		[Chroma] = server,
	};
}
