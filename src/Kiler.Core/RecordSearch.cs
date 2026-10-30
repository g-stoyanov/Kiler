using System.Globalization;
using System.Text;

namespace Kiler.Core;

/// <summary>The query of the list (M5.6, docs/api.md, <c>GET /api/records</c>): every part optional.</summary>
public sealed class RecordQuery
{
    /// <summary>The search text: a record matches when its search text has every word of it, without regard to case.</summary>
    public string? Text { get; init; }

    /// <summary>The path of the chosen node, 0 to <see cref="RecordFile.MaxStashes"/> ids: a record matches when its path starts with it.</summary>
    public IReadOnlyList<string> Stash { get; init; } = [];

    /// <summary>The id of a template.</summary>
    public string? Template { get; init; }

    /// <summary>The tags that a record has every one of, without regard to case.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>A value of a field with <c>filter</c> by key: the text of the value that the session reads, without regard to case.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>A node of the tree of stashes: a stash at its place in the paths, and how many records are at it and under it.</summary>
public sealed class StashNode
{
    internal StashNode(string id, string? name, int count, IReadOnlyList<StashNode> children)
    {
        Id = id;
        Name = name;
        Count = count;
        Children = children;
    }

    public string Id { get; }

    /// <summary>The name of the stash; null when it cannot be read, as it is missing or does not fit.</summary>
    public string? Name { get; }

    public int Count { get; }

    /// <summary>The nodes under it, by name without regard to case, a stash that cannot be read last.</summary>
    public IReadOnlyList<StashNode> Children { get; }
}

/// <summary>A value of a facet, a template id, a tag or the text of a value, and how many records it gives.</summary>
public sealed class FacetValue
{
    internal FacetValue(string value, int count)
    {
        Value = value;
        Count = count;
    }

    /// <summary>The text; the value of a field that the session reads, so it is no <c>secret</c>.</summary>
    public string Value { get; }

    public int Count { get; }
}

/// <summary>The values of a field with <c>filter</c> that the records give, by key.</summary>
public sealed class FieldFacet
{
    internal FieldFacet(string key, IReadOnlyList<FacetValue> values)
    {
        Key = key;
        Values = values;
    }

    public string Key { get; }

    public IReadOnlyList<FacetValue> Values { get; }
}

/// <summary>What the list shows for a query (<see cref="RecordSearch.Find"/>): the records, the tree and the facets.</summary>
public sealed class SearchResult
{
    internal SearchResult(IReadOnlyList<RecordView> records, IReadOnlyList<StashNode> tree, IReadOnlyList<FacetValue> templates, IReadOnlyList<FacetValue> tags, IReadOnlyList<FieldFacet> fields)
    {
        Records = records;
        Tree = tree;
        Templates = templates;
        Tags = tags;
        Fields = fields;
    }

    /// <summary>Every record that matches everything, by name without regard to case.</summary>
    public IReadOnlyList<RecordView> Records { get; }

    /// <summary>The nodes of the paths of the records that match everything but the stash, by name (design 6.6).</summary>
    public IReadOnlyList<StashNode> Tree { get; }

    /// <summary>The templates of the records that match everything but the template, by id.</summary>
    public IReadOnlyList<FacetValue> Templates { get; }

    /// <summary>The tags of the records that match everything, the tags chosen too, by tag without regard to case.</summary>
    public IReadOnlyList<FacetValue> Tags { get; }

    /// <summary>The fields with <c>filter</c>, by key, each counting the records that match everything but its own value.</summary>
    public IReadOnlyList<FieldFacet> Fields { get; }
}

/// <summary>
/// The search, the filters, the tree and the facets of the list (M5.6, design 11, docs/api.md): a pass over the records of a
/// <see cref="RecordSet"/>, as one session reads them, without an index of words, as 2000 records take about a millisecond.
/// The search text of each record is made once per set (<see cref="SearchIndex"/>), and each change of the share, a sign-in
/// and a lock give a new set, so the search follows them. It reads the values only from the <see cref="RecordView"/>, never
/// a <c>secret</c> (core.md).
/// </summary>
public static class RecordSearch
{
    /// <summary>The records of <paramref name="set"/> that match <paramref name="query"/>, the tree and the facets.</summary>
    public static SearchResult Find(RecordSet set, RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(query);
        var words = (query.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToUpperInvariant()).ToArray();
        var records = new List<RecordView>();
        var tree = new Node();
        var templates = new Counts(StringComparer.Ordinal);
        var tags = new Counts(StringComparer.OrdinalIgnoreCase);
        var fields = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        void Count(string key, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (!fields.TryGetValue(key, out var counts))
            {
                fields[key] = counts = new(StringComparer.OrdinalIgnoreCase);
            }

            counts.Add(value);
        }

        foreach (var entry in set.Index.Entries)
        {
            if (!words.All(word => entry.Text.Contains(word, StringComparison.Ordinal)))
            {
                continue;
            }

            // What the record misses of the rest: the facet of the one part that it alone misses counts it (docs/api.md). A
            // record that misses only the tags counts nowhere, as the tags count with the tags chosen.
            var missesStash = !StartsWith(entry.File.Stash, query.Stash);
            var missesTemplate = query.Template is { } template && !string.Equals(entry.File.Template, template, StringComparison.Ordinal);
            var missesTags = !query.Tags.All(tag => entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            var missedFields = 0;
            string? missedField = null;
            foreach (var (key, value) in query.Fields)
            {
                if (!(entry.Filters.TryGetValue(key, out var text) && string.Equals(text, value, StringComparison.OrdinalIgnoreCase)))
                {
                    missedFields++;
                    missedField = key;
                }
            }

            switch ((missesStash ? 1 : 0) + (missesTemplate ? 1 : 0) + (missesTags ? 1 : 0) + missedFields)
            {
                case 0:
                    records.Add(entry.View);
                    tree.Add(entry.File.Stash);
                    templates.Add(entry.File.Template);
                    foreach (var tag in entry.Tags)
                    {
                        tags.Add(tag);
                    }

                    foreach (var (key, text) in entry.Filters)
                    {
                        Count(key, text);
                    }

                    break;
                case 1 when missesStash:
                    tree.Add(entry.File.Stash);
                    break;
                case 1 when missesTemplate:
                    templates.Add(entry.File.Template);
                    break;
                case 1 when missedField is not null:
                    Count(missedField, entry.Filters.GetValueOrDefault(missedField));
                    break;
            }
        }

        return new(
            [.. records.OrderBy(view => view.File.Model!.Name, StringComparer.OrdinalIgnoreCase).ThenBy(view => view.File.Model!.Id, StringComparer.Ordinal)],
            tree.Nodes(set),
            templates.Values(),
            tags.Values(),
            [.. fields.Select(field => new FieldFacet(field.Key, field.Value.Values()))]);
    }

    /// <summary>
    /// The text of a value, for the search, the filters and the facets (docs/api.md): a text as it is, a number as JSON
    /// writes it, the code of a code without its language, and the name of the record of a <c>ref</c>, null when that record
    /// has gone.
    /// </summary>
    internal static string? TextOf(FieldValue value, FieldType type, RecordSet set) => value.Kind switch
    {
        FieldValueKind.Number => value.Number.ToString(CultureInfo.InvariantCulture),
        FieldValueKind.Code => value.Text,
        _ => type == FieldType.Ref ? set.RecordName(value.Text!) : value.Text,
    };

    private static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> start) =>
        path.Count >= start.Count && start.Select((id, position) => string.Equals(path[position], id, StringComparison.Ordinal)).All(same => same);

    /// <summary>Counts of texts, each once in the comparer's sense, in the spelling met first.</summary>
    private sealed class Counts(StringComparer comparer)
    {
        private readonly Dictionary<string, int> _counts = new(comparer);
        private readonly StringComparer _comparer = comparer;

        public void Add(string value) => _counts[value] = _counts.GetValueOrDefault(value) + 1;

        public IReadOnlyList<FacetValue> Values() =>
            [.. _counts.OrderBy(count => count.Key, _comparer).ThenBy(count => count.Key, StringComparer.Ordinal).Select(count => new FacetValue(count.Key, count.Value))];
    }

    /// <summary>A node of the tree as it is counted: the records at it and under it, and its nodes by stash id.</summary>
    private sealed class Node
    {
        private readonly Dictionary<string, Node> _children = new(StringComparer.Ordinal);
        private int _count;

        public void Add(IReadOnlyList<string> path)
        {
            var node = this;
            foreach (var id in path)
            {
                if (!node._children.TryGetValue(id, out var child))
                {
                    node._children[id] = child = new();
                }

                child._count++;
                node = child;
            }
        }

        public IReadOnlyList<StashNode> Nodes(RecordSet set) =>
        [
            .. _children
                .Select(child => new StashNode(child.Key, set.StashName(child.Key), child.Value._count, child.Value.Nodes(set)))
                .OrderBy(node => node.Name is null)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Id, StringComparer.Ordinal),
        ];
    }
}

/// <summary>
/// What the search reads of the records of one <see cref="RecordSet"/>, made at its first search and kept with it: each
/// record whose file fits, with its search text and the texts of its fields with <c>filter</c>. A record whose file does not
/// fit is left out, as it has no name or path to show (decided in M5.6).
/// </summary>
internal sealed class SearchIndex
{
    public SearchIndex(RecordSet set)
    {
        var entries = new List<SearchEntry>(set.Records.Count);
        foreach (var view in set.Records.Values)
        {
            if (view.File.Model is { } file)
            {
                entries.Add(new(view, file, set));
            }
        }

        Entries = entries;
    }

    /// <summary>The records by id.</summary>
    public IReadOnlyList<SearchEntry> Entries { get; }
}

/// <summary>A record as the search reads it; its texts hold the values that the session reads, never a <c>secret</c>.</summary>
internal sealed class SearchEntry
{
    public SearchEntry(RecordView view, RecordFile file, RecordSet set)
    {
        View = view;
        File = file;

        // The parts on lines of their own, so a word, which has no white space, never spans two of them.
        var text = new StringBuilder(file.Name);
        void Add(string? part)
        {
            if (part is not null)
            {
                text.Append('\n').Append(part);
            }
        }

        if (view.Description is { Kind: FieldKind.Value, Value: { } description })
        {
            Add(RecordSearch.TextOf(description, FieldType.Text, set));
        }

        foreach (var tag in file.Tags)
        {
            Add(tag);
        }

        foreach (var id in file.Stash)
        {
            Add(set.StashName(id));
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (view.Template is { } template)
        {
            // The fields of the view are those of its template, in its order. A secret is never a value of the view; the
            // type is checked too, so a value of a secret never comes here.
            foreach (var (field, read) in template.Fields.Zip(view.Fields))
            {
                if (field.Type == FieldType.Secret || read is not { Kind: FieldKind.Value, Value: { } value } || RecordSearch.TextOf(value, field.Type, set) is not { } valueText)
                {
                    continue;
                }

                Add(valueText);
                if (field.Filter)
                {
                    filters[field.Key] = valueText;
                }
            }
        }

        Text = text.ToString().ToUpperInvariant();
        Tags = [.. file.Tags.Distinct(StringComparer.OrdinalIgnoreCase)];
        Filters = filters;
    }

    public RecordView View { get; }

    public RecordFile File { get; }

    /// <summary>The search text in upper case, which a word in upper case is looked for in, ordinal.</summary>
    public string Text { get; }

    /// <summary>The tags, each once without regard to case.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>The text of each field with <c>filter</c> that the session reads, by key.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; }

    public override string ToString() => File.Id;
}
