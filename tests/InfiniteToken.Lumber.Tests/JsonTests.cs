using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class JsonTests
{
    private enum Plan
    {
        Hobby,
        Pro,
    }

    [Fact]
    public void AnonymousObjectsAndClasses()
    {
        var value = new { userId = 123, order = new Order { Id = 7, Total = 12.5m, Items = new[] { "a", "b" } }, plan = Plan.Pro };
        JsonAssert.Equal(
            "{\"userId\":123,\"order\":{\"Id\":7,\"Total\":12.5,\"Items\":[\"a\",\"b\"]},\"plan\":\"Pro\"}",
            Write(value));
    }

    [Fact]
    public void Dictionaries()
    {
        var value = new Dictionary<string, object?>
        {
            ["counts"] = new Dictionary<string, int> { ["a"] = 1 },
            ["byId"] = new Dictionary<int, string> { [7] = "seven" },
            ["readOnly"] = new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool> { ["on"] = true }),
            ["none"] = null,
        };
        JsonAssert.Equal("{\"counts\":{\"a\":1},\"byId\":{\"7\":\"seven\"},\"readOnly\":{\"on\":true},\"none\":null}", Write(value));
    }

    [Fact]
    public void ValuesJsonHasNoTypeFor()
    {
        var when = new DateTimeOffset(2026, 11, 9, 10, 30, 0, TimeSpan.Zero);
        var value = new
        {
            when,
            took = TimeSpan.FromMilliseconds(2400),
            id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
            url = new Uri("https://example.com/a?b=c"),
            ip = IPAddress.Loopback,
            error = new InvalidOperationException("Payment declined"),
            bytes = new byte[] { 1, 2, 3 },
            type = typeof(string),
            letter = 'x',
            text = new StringBuilder("built"),
        };
        JsonAssert.Equal(
            "{\"when\":\"2026-11-09T10:30:00.0000000+00:00\",\"took\":\"00:00:02.4000000\",\"id\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\","
            + "\"url\":\"https://example.com/a?b=c\",\"ip\":\"127.0.0.1\",\"error\":\"System.InvalidOperationException: Payment declined\","
            + "\"bytes\":\"AQID\",\"type\":\"System.String\",\"letter\":\"x\",\"text\":\"built\"}",
            Write(value));
    }

    [Fact]
    public void NumbersJsonCantHoldAreNull()
    {
        JsonAssert.Equal(
            "{\"nan\":null,\"infinity\":null,\"small\":null,\"half\":0.5,\"big\":1E+300,\"long\":9007199254740993,\"decimal\":0.1}",
            Write(new { nan = double.NaN, infinity = double.PositiveInfinity, small = float.NegativeInfinity, half = 0.5f, big = 1e300, @long = 9007199254740993L, @decimal = 0.1m }));
    }

    [Fact]
    public void StructuresThatContainThemselves()
    {
        var list = new List<object?> { 1 };
        list.Add(list);
        var map = new Dictionary<string, object?> { ["name"] = "loop" };
        map["self"] = map;
        var shared = new { x = 1 };
        JsonAssert.Equal(
            "{\"list\":[1,\"[Circular]\"],\"map\":{\"name\":\"loop\",\"self\":\"[Circular]\"},\"twice\":[{\"x\":1},{\"x\":1}]}",
            Write(new { list, map, twice = new[] { shared, shared } }));
    }

    [Fact]
    public void DeepStructuresStop()
    {
        object value = "bottom";
        for (var i = 0; i < 100; i++) value = new List<object?> { value };
        var text = Encoding.UTF8.GetString(Json.Serialize(Json.Normalize(value)));
        Assert.Contains("[Too deep]", text);
        JsonAssert.Parse(text);
    }

    [Fact]
    public void AGetterThatThrows()
    {
        JsonAssert.Equal("{\"Fine\":1,\"Broken\":\"[Unreadable: no]\"}", Write(new Throws()));
    }

    [Fact]
    public void StringsAreEscaped()
    {
        var text = "quote \" backslash \\ newline \n tab \t nul \u0000 bell \u0007 é ☕ 🚨 \u2028";
        var json = Write(new { text });
        Assert.Equal(text, json.GetProperty("text").GetString());
        Assert.Contains("é ☕ 🚨", Encoding.UTF8.GetString(Json.Serialize(Json.Normalize(new { text }))));
    }

    [Fact]
    public void ALoneSurrogateStillMakesValidUtf8()
    {
        var bytes = Json.Serialize(Json.Normalize(new { text = "a\ud800b" }));
        Assert.Equal("a�b", JsonAssert.Parse(bytes).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"message\":\"This team has sent its 10,000 logs\"}", "This team has sent its 10,000 logs")]
    [InlineData("{\"message\":\"caf\\u00e9 \\\"x\\\"\",\"code\":\"X\"}", "café \"x\"")]
    [InlineData("  {\"other\":1}  ", null)]
    [InlineData("{\"message\":7}", null)]
    [InlineData("<html>Bad gateway</html>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadsTheApisMessage(string? body, string? message) => Assert.Equal(message, Json.MessageOf(body));

    [Fact]
    public void ParsesWhatItWrites()
    {
        var text = "{\"a\":[1,-2.5e-3,true,false,null,\"\\u00e9\\n\"],\"b\":{},\"c\":[],\"d\":12345678901234567890}";
        var parsed = Json.Parse(text);
        JsonAssert.Equal(text, JsonAssert.Parse(Json.Serialize(parsed)));
        // Big numbers are sent exactly as they came
        Assert.Contains("12345678901234567890", Encoding.UTF8.GetString(Json.Serialize(parsed)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("01")]
    [InlineData("\"open")]
    [InlineData("tru")]
    [InlineData("{} x")]
    public void RefusesWhatIsntJson(string text) => Assert.Throws<FormatException>(() => Json.Parse(text));

#if NET
    [Fact]
    public void SystemTextJsonValues()
    {
        using var document = JsonDocument.Parse("{\"rows\":[1,2],\"price\":19.990}");
        var node = System.Text.Json.Nodes.JsonNode.Parse("{\"on\":true}");
        JsonAssert.Equal(
            "{\"element\":{\"rows\":[1,2],\"price\":19.990},\"node\":{\"on\":true},\"date\":\"2026-11-09\"}",
            Write(new { element = document.RootElement, node, date = new DateOnly(2026, 11, 9) }));
    }
#endif

    private static JsonElement Write(object value) => JsonAssert.Parse(Json.Serialize(Json.Normalize(value)));

    private sealed class Order
    {
        public int Id { get; set; }

        public decimal Total { get; set; }

        public string[] Items { get; set; } = Array.Empty<string>();

        public string this[int index] => Items[index];

        internal string Hidden { get; set; } = "never sent";
    }

    private sealed class Throws
    {
        public int Fine => 1;

        public int Broken => throw new InvalidOperationException("no");
    }
}
