using System.Text.Json;

namespace CouchbaseInsight.Models;

/// <summary>
/// One document as Load reads it: its key, its body, and two metadata fields that come with it at
/// no extra query — the expiry (epoch seconds, 0 = none) and the CAS (a nanosecond clock of its
/// last write).
/// </summary>
public sealed record DocumentRow(string Key, JsonElement Document, long Expiration, ulong Cas);
