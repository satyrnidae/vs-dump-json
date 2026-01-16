using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Serialization;

namespace DumpJson;

public class DuplicateFieldResolvingContractResolver : DefaultContractResolver {
  protected override List<MemberInfo> GetSerializableMembers(Type objectType) {
    List<MemberInfo> members = base.GetSerializableMembers(objectType);
    Dictionary<string, MemberInfo> seen = new(StringComparer.OrdinalIgnoreCase);

    return members.Where(m => {
      string name = m.Name.ToLowerInvariant();
      return seen.TryAdd(name, m);
    }).ToList();
  }
}
