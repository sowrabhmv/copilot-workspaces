using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Workspace.Server;

public static partial class SpecValidator
{
    private static readonly HashSet<string> ArtifactTypes =
        ["Section", "Heading", "Text", "BulletList", "DataTable", "Decision"];
    private static readonly HashSet<string> ClarificationTypes =
        ["Section", "Heading", "Text", "ChoiceQuestion", "TextQuestion", "MultiChoiceQuestion",
         "SliderQuestion", "NumberQuestion", "ToggleQuestion", "DateQuestion"];

    public static bool IsQuestion(string type) => type is "ChoiceQuestion" or "TextQuestion" or
        "MultiChoiceQuestion" or "SliderQuestion" or "NumberQuestion" or "ToggleQuestion" or "DateQuestion";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsId(string? value) =>
        value is not null && IdPattern().IsMatch(value) &&
        value is not ("constructor" or "prototype" or "__proto__");

    public static void Validate(UiSpec? spec, string kind)
    {
        Require(kind is "artifact" or "clarification", "Unknown document kind.");
        Require(spec?.Elements is not null && IsId(spec.Root), "A document needs a valid root.");
        var document = spec!;
        Require(document.Elements.Count is >= 1 and <= 96, "Documents must have 1-96 elements.");
        try
        {
            Require(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document, JsonDefaults.Options)) <= 196608,
                "The document exceeds the 192 KiB limit.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new WorkspaceException("invalid_spec", "The document must contain bounded literal JSON values.");
        }
        Require(document.Elements.TryGetValue(document.Root, out var root) && root?.Type == "Section",
            "The document root must be a Section.");

        var allowed = kind == "artifact" ? ArtifactTypes : ClarificationTypes;
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);
        var questions = 0;
        foreach (var (id, element) in document.Elements)
        {
            Require(IsId(id) && element is not null, "Element identifiers must be safe and unique.");
            Require(element!.Type is not null && allowed.Contains(element.Type), "An unsupported component was generated.");
            Require(element.Props is not null && element.Children is not null, "Every element needs props and children.");
            var props = element.Props!;
            Require(Text(props, "anchorId", 64) == id, "Element anchors must match their identifiers.");
            Require(element.Children!.Count <= 24, "A section has too many children.");
            Require(element.Type == "Section" || element.Children.Count == 0, "Only Sections can have children.");
            foreach (var child in element.Children)
            {
                Require(IsId(child) && document.Elements.ContainsKey(child), "A child refers to a missing element.");
                parents[child] = parents.GetValueOrDefault(child) + 1;
                Require(parents[child] == 1 && child != document.Root, "Elements must have exactly one parent.");
            }

            switch (element.Type)
            {
                case "Section":
                    Keys(props, "anchorId", "title", "description");
                    Text(props, "title", 180);
                    NullableText(props, "description", 500);
                    break;
                case "Heading":
                    Keys(props, "anchorId", "level", "text");
                    Require(Text(props, "level", 2) is "h2" or "h3" or "h4", "Unsupported heading level.");
                    Text(props, "text", 180);
                    break;
                case "Text":
                    Keys(props, "anchorId", "text");
                    Text(props, "text", 8000);
                    break;
                case "BulletList":
                    Keys(props, "anchorId", "ordered", "items");
                    Boolean(props, "ordered");
                    var items = Objects(props, "items", 1, 40);
                    UniqueIds(items);
                    foreach (var item in items)
                    {
                        Keys(item, "id", "text");
                        Text(item, "text", 2000);
                    }
                    break;
                case "DataTable":
                    Keys(props, "anchorId", "caption", "columns", "rows");
                    NullableText(props, "caption", 180);
                    var columns = Options(props, "columns", 1, 8, false);
                    var rows = Objects(props, "rows", 0, 30);
                    UniqueIds(rows);
                    foreach (var row in rows)
                    {
                        Keys(row, "id", "cells");
                        Require(row["cells"] is JsonArray cells && cells.Count == columns.Count,
                            "Every table row must match its columns.");
                        foreach (var cell in (JsonArray)row["cells"]!)
                            Require(cell is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 2000,
                                "Table cells must contain bounded plain text.");
                    }
                    break;
                case "Decision":
                    Keys(props, "anchorId", "question", "options", "recommendedId");
                    Text(props, "question", 180);
                    var choices = Options(props, "options", 2, 6, true);
                    var recommendation = NullableText(props, "recommendedId", 64);
                    Require(recommendation is null || choices.Contains(recommendation), "The recommendation must name an option.");
                    break;
                case "ChoiceQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "options", "value");
                    Question(props);
                    var options = Options(props, "options", 2, 8, false);
                    var value = NullableText(props, "value", 64);
                    Require(value is null || options.Contains(value), "A choice default must name an option.");
                    questions++;
                    break;
                case "TextQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "multiline", "value");
                    Question(props);
                    Boolean(props, "multiline");
                    NullableText(props, "value", 4000);
                    questions++;
                    break;
                case "MultiChoiceQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "options", "value");
                    Question(props);
                    var multipleOptions = Options(props, "options", 2, 8, false);
                    if (props["value"] is not null)
                    {
                        var defaults = StringArray(props["value"], 0, 8, 64);
                        Require(defaults.Distinct(StringComparer.Ordinal).Count() == defaults.Count &&
                            defaults.All(multipleOptions.Contains), "Multi-choice defaults must be distinct offered options.");
                    }
                    questions++;
                    break;
                case "SliderQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "min", "max", "step",
                        "value", "minLabel", "maxLabel", "unit", "labels");
                    Question(props);
                    var minimum = Number(props, "min", 1000000);
                    var maximum = Number(props, "max", 1000000);
                    var step = Number(props, "step", 2000000);
                    Require(minimum < maximum && step > 0 && Aligned(maximum, minimum, step),
                        "A slider needs ordered bounds and evenly spaced positive steps.");
                    var count = (maximum - minimum) / step;
                    Require(count is >= 1 and <= 10000, "A slider must have 1-10000 steps.");
                    NullableText(props, "minLabel", 80);
                    NullableText(props, "maxLabel", 80);
                    NullableText(props, "unit", 80);
                    var suggested = NullableNumber(props, "value", 1000000);
                    Require(suggested is null || NumberFits(suggested.Value, minimum, maximum, step),
                        "The suggested slider value must fit its bounds and steps.");
                    if (props["labels"] is not null)
                        Require(StringArray(props["labels"], 2, 11, 80).Count == (int)Math.Round(count) + 1,
                            "Slider labels must cover each step including its endpoints.");
                    questions++;
                    break;
                case "NumberQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "min", "max", "step", "value", "unit");
                    Question(props);
                    var numberMinimum = NullableNumber(props, "min", 1000000000000);
                    var numberMaximum = NullableNumber(props, "max", 1000000000000);
                    var numberStep = Number(props, "step", 1000000000000);
                    Require(numberStep > 0 && (numberMinimum is null || numberMaximum is null || numberMinimum <= numberMaximum),
                        "A number input needs a positive step and ordered bounds.");
                    NullableText(props, "unit", 80);
                    var numberDefault = NullableNumber(props, "value", 1000000000000);
                    Require(numberDefault is null || NumberFits(numberDefault.Value, numberMinimum, numberMaximum, numberStep),
                        "The suggested number must fit its bounds and step.");
                    questions++;
                    break;
                case "ToggleQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "value");
                    Question(props);
                    Boolean(props, "value");
                    questions++;
                    break;
                case "DateQuestion":
                    Keys(props, "anchorId", "label", "help", "required", "min", "max", "value");
                    Question(props);
                    var dateMinimum = NullableDate(props, "min");
                    var dateMaximum = NullableDate(props, "max");
                    var dateDefault = NullableDate(props, "value");
                    Require(dateMinimum is null || dateMaximum is null || dateMinimum <= dateMaximum,
                        "The earliest date must not be after the latest date.");
                    Require(dateDefault is null ||
                        (dateMinimum is null || dateDefault >= dateMinimum) &&
                        (dateMaximum is null || dateDefault <= dateMaximum),
                        "The suggested date must be within its bounds.");
                    questions++;
                    break;
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(document.Root, 1);
        Require(visited.Count == document.Elements.Count, "Documents cannot contain disconnected elements.");
        if (kind == "clarification")
            Require(questions is >= 1 and <= 8, "Clarification needs 1-8 questions.");
        return;

        void Visit(string id, int depth)
        {
            Require(depth <= 8 && visited.Add(id), "The document is cyclic or exceeds eight levels.");
            foreach (var child in document.Elements[id].Children)
                Visit(child, depth + 1);
        }
    }

    public static Dictionary<string, string> ValidateAnswers(UiSpec spec, Dictionary<string, string>? answers)
    {
        Validate(spec, "clarification");
        if (answers is null || answers.Count > 8)
            throw new WorkspaceException("invalid_answers", "Supply answers for this clarification form.");
        var questions = spec.Elements.Where(e => IsQuestion(e.Value.Type))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        if (answers.Keys.Any(id => !questions.ContainsKey(id)))
            throw new WorkspaceException("invalid_answers", "An answer does not belong to this form.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, element) in questions)
        {
            var answer = answers.GetValueOrDefault(id)?.Trim() ?? "";
            var label = Text(element.Props, "label", 180);
            if (answer.Length > 4000)
                throw new WorkspaceException("invalid_answers", $"The answer for {label} is too long.");
            if (answer.Length == 0)
            {
                if (Boolean(element.Props, "required"))
                    throw new WorkspaceException("invalid_answers", $"An answer is required for {label}.");
                result[id] = element.Type == "MultiChoiceQuestion" ? "[]" : "";
                continue;
            }
            result[id] = ValidateAnswer(element, answer);
        }
        return result;
    }

    public static string? ElementText(UiElement element) =>
        element.Type switch
        {
            "Text" or "Heading" => element.Props["text"]?.GetValue<string>(),
            "Section" => element.Props["title"]?.GetValue<string>(),
            "Decision" => element.Props["question"]?.GetValue<string>(),
            _ when IsQuestion(element.Type) => element.Props["label"]?.GetValue<string>(),
            "DataTable" => element.Props["caption"]?.GetValue<string>(),
            _ => null
        };

    private static void Question(JsonObject props)
    {
        Text(props, "label", 180);
        NullableText(props, "help", 500);
        Boolean(props, "required");
    }

    private static string ValidateAnswer(UiElement element, string answer)
    {
        var props = element.Props;
        switch (element.Type)
        {
            case "ChoiceQuestion":
                if (!Options(props, "options", 2, 8, false).Contains(answer))
                    throw new WorkspaceException("invalid_answers", "Choose one of the displayed options.");
                break;
            case "MultiChoiceQuestion":
                List<string>? selected;
                try { selected = JsonSerializer.Deserialize<List<string>>(answer, JsonDefaults.Options); }
                catch (JsonException) { throw new WorkspaceException("invalid_answers", "Submit multi-select answers as an array of offered option IDs."); }
                var options = Options(props, "options", 2, 8, false);
                if (selected is null || selected.Count > options.Count ||
                    selected.Any(item => item is null || !options.Contains(item)) ||
                    selected.Distinct(StringComparer.Ordinal).Count() != selected.Count ||
                    Boolean(props, "required") && selected.Count == 0)
                    throw new WorkspaceException("invalid_answers", "Select distinct offered options; required questions need at least one.");
                return JsonSerializer.Serialize(selected);
            case "SliderQuestion":
            case "NumberQuestion":
                var bound = element.Type == "SliderQuestion" ? 1000000d : 1000000000000d;
                if (!double.TryParse(answer, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number) || Math.Abs(number) > bound ||
                    !NumberFits(number, NullableNumber(props, "min", bound), NullableNumber(props, "max", bound),
                        Number(props, "step", element.Type == "SliderQuestion" ? 2000000 : bound)))
                    throw new WorkspaceException("invalid_answers", "Choose a number within the displayed bounds and step.");
                return number.ToString("R", CultureInfo.InvariantCulture);
            case "ToggleQuestion":
                if (answer is not ("true" or "false"))
                    throw new WorkspaceException("invalid_answers", "Submit a switch choice as true or false.");
                break;
            case "DateQuestion":
                var minimum = NullableDate(props, "min");
                var maximum = NullableDate(props, "max");
                if (!TryDate(answer, out var date) || minimum is not null && date < minimum ||
                    maximum is not null && date > maximum)
                    throw new WorkspaceException("invalid_answers", "Choose a valid date within the displayed bounds.");
                break;
        }
        return answer;
    }

    private static double Number(JsonObject props, string key, double bound)
    {
        Require(props[key] is JsonValue node && node.GetValueKind() == JsonValueKind.Number,
            $"Invalid numeric property: {key}.");
        var number = JsonSerializer.Deserialize<double>(props[key]!.ToJsonString());
        Require(double.IsFinite(number) && Math.Abs(number) <= bound, $"Numeric property {key} exceeds its supported bounds.");
        return number;
    }

    private static double? NullableNumber(JsonObject props, string key, double bound)
    {
        Require(props.ContainsKey(key), $"Missing property: {key}.");
        return props[key] is null ? null : Number(props, key, bound);
    }

    private static bool NumberFits(double value, double? minimum, double? maximum, double step) =>
        (minimum is null || value >= minimum) && (maximum is null || value <= maximum) &&
        Aligned(value, minimum ?? 0, step);

    private static bool Aligned(double value, double minimum, double step)
    {
        if (!(step > 0)) return false;
        var steps = (value - minimum) / step;
        return double.IsFinite(steps) && Math.Abs(steps - Math.Round(steps)) <= 0.0000001;
    }

    private static List<string> StringArray(JsonNode? node, int minimum, int maximum, int textLimit)
    {
        Require(node is JsonArray array && array.Count >= minimum && array.Count <= maximum,
            "The control contains an invalid value list.");
        var result = new List<string>();
        foreach (var item in (JsonArray)node!)
        {
            Require(item is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text) && text.Length <= textLimit, "Control list entries must be bounded text.");
            result.Add(item!.GetValue<string>());
        }
        return result;
    }

    private static DateOnly? NullableDate(JsonObject props, string key)
    {
        var text = NullableText(props, key, 10);
        if (text is null) return null;
        Require(TryDate(text, out var date), "Dates must be valid ISO YYYY-MM-DD values.");
        return date;
    }

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
        text.Length == 10;

    private static HashSet<string> Options(JsonObject props, string key, int min, int max, bool reasons)
    {
        var options = Objects(props, key, min, max);
        var ids = UniqueIds(options);
        foreach (var option in options)
        {
            if (reasons) Keys(option, "id", "label", "reason");
            else Keys(option, "id", "label");
            Text(option, "label", 180);
            if (reasons) Text(option, "reason", 2000);
        }
        return ids;
    }

    private static List<JsonObject> Objects(JsonObject props, string key, int min, int max)
    {
        Require(props[key] is JsonArray array && array.Count >= min && array.Count <= max,
            $"Invalid collection: {key}.");
        var result = new List<JsonObject>();
        foreach (var item in (JsonArray)props[key]!)
        {
            Require(item is JsonObject, $"Entries in {key} must be objects.");
            result.Add((JsonObject)item!);
        }
        return result;
    }

    private static HashSet<string> UniqueIds(IEnumerable<JsonObject> values)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var id = Text(value, "id", 64);
            Require(IsId(id) && ids.Add(id), "Collection identifiers must be safe and unique.");
        }
        return ids;
    }

    private static void Keys(JsonObject props, params string[] names)
    {
        Require(props.Count == names.Length && props.All(p => names.Contains(p.Key, StringComparer.Ordinal)),
            "A component has missing or unsupported properties.");
    }

    private static string Text(JsonObject props, string key, int max)
    {
        Require(props[key] is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text) && text.Length <= max, $"Invalid text property: {key}.");
        return props[key]!.GetValue<string>();
    }

    private static string? NullableText(JsonObject props, string key, int max)
    {
        Require(props.ContainsKey(key), $"Missing property: {key}.");
        if (props[key] is null) return null;
        Require(props[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= max,
            $"Invalid nullable text property: {key}.");
        return props[key]!.GetValue<string>();
    }

    private static bool Boolean(JsonObject props, string key)
    {
        Require(props[key] is JsonValue value && value.TryGetValue<bool>(out _), $"Invalid boolean property: {key}.");
        return props[key]!.GetValue<bool>();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new WorkspaceException("invalid_spec", message);
    }
}
