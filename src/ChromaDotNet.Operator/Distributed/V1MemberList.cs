using System.Text.Json.Serialization;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace ChromaDotNet.Operator.Distributed;

/// <summary>
/// A MemberList of distributed Chroma (memberlists.chroma.cluster): the sysdb leader lists in it the ready Pods of a component, and
/// the other components read it to find them. The CRD is the one of the chart, in deploy/memberlist-crd.yaml: not generated from here.
/// </summary>
[Ignore]
[KubernetesEntity(Group = "chroma.cluster", ApiVersion = "v1", Kind = "MemberList", PluralName = "memberlists")]
public sealed class V1MemberList : CustomKubernetesEntity<V1MemberList.MemberListSpec>
{
	public V1MemberList()
	{
		ApiVersion = "chroma.cluster/v1";
		Kind = "MemberList";
	}

	public sealed class MemberListSpec
	{
		[JsonPropertyName("members")]
		public IList<Member>? Members { get; set; }
	}

	public sealed class Member
	{
		[JsonPropertyName("member_id")]
		public string? MemberId { get; set; }

		[JsonPropertyName("member_ip")]
		public string? MemberIp { get; set; }

		[JsonPropertyName("member_node_name")]
		public string? MemberNodeName { get; set; }
	}
}
