using System.Text.Json;

namespace CouchbaseInsight.Models;

/// <summary>
/// One value of one field of one class within one key range, as the server counted it: how many
/// documents of the class hold that value. <see cref="Class"/> is null for documents without a
/// class. The per-field totals (distinct values in the range, documents holding the field, min and
/// max) come with every row when the server can compute them in the same query, and are null when
/// the app has to work them out from the rows. <see cref="ExampleKey"/> is one document holding
/// the value (the lowest key among them in the range).
/// </summary>
public sealed record FieldValueRow(
    string? Class, string Field, JsonElement Value, long Count,
    long? GroupsInRange, long? NonMissingInRange, JsonElement? Min, JsonElement? Max,
    string? ExampleKey = null);
