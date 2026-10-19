using System.Collections.Generic;
using NUnit.Framework;

namespace LetiArts.PlaytestTelemetry.Tests
{
    public class JsonWriterTests
    {
        [Test]
        public void Escapes_Quotes_Backslashes_And_ControlCharacters()
        {
            var w = new JsonWriter();
            w.Value("a\"b\\c\n\r\t\b\f\u0001");
            Assert.AreEqual("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\"", w.ToString());
        }

        [Test]
        public void Unicode_PassesThrough_And_LoneSurrogatesAreEscaped()
        {
            var w = new JsonWriter();
            w.Value("héllo 日本 😀");
            Assert.AreEqual("\"héllo 日本 😀\"", w.ToString());

            w.Clear();
            w.Value("bad\ud800end");
            Assert.AreEqual("\"bad\\ud800end\"", w.ToString());
        }

        [Test]
        public void Object_WithMixedValues()
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("s", "x").Prop("i", 5).Prop("d", 1.5).Prop("b", true).Name("n").Null().EndObject();
            Assert.AreEqual("{\"s\":\"x\",\"i\":5,\"d\":1.5,\"b\":true,\"n\":null}", w.ToString());
        }

        [Test]
        public void NestedDictionariesAndLists()
        {
            var props = new Dictionary<string, object>
            {
                { "level", 3 },
                { "name", "a\"b" },
                { "ratio", 0.25f },
                { "tags", new List<object> { "x", 2, true, null } },
                { "inner", new Dictionary<string, object> { { "deep", new Dictionary<string, object> { { "k", "v" } } } } },
            };
            var w = new JsonWriter();
            w.Value((object)props);
            Assert.AreEqual(
                "{\"level\":3,\"name\":\"a\\\"b\",\"ratio\":0.25,\"tags\":[\"x\",2,true,null],\"inner\":{\"deep\":{\"k\":\"v\"}}}",
                w.ToString());
        }

        [Test]
        public void ArraysOfObjects_GetCommasRight()
        {
            var w = new JsonWriter();
            w.BeginArray();
            for (int i = 0; i < 3; i++) w.BeginObject().Prop("i", i).EndObject();
            w.EndArray();
            Assert.AreEqual("[{\"i\":0},{\"i\":1},{\"i\":2}]", w.ToString());
        }

        [Test]
        public void NonFiniteNumbersBecomeNull_And_NumbersUseInvariantCulture()
        {
            var w = new JsonWriter();
            w.BeginArray().Value(double.NaN).Value(double.PositiveInfinity).Value(0.1).Value(1e20).EndArray();
            Assert.AreEqual("[null,null,0.1,1E+20]", w.ToString());
        }

        [Test]
        public void Raw_IsEmbeddedVerbatim()
        {
            var w = new JsonWriter();
            w.BeginObject().PropRaw("crumbs", "[1,2]").PropRaw("none", null).EndObject();
            Assert.AreEqual("{\"crumbs\":[1,2],\"none\":null}", w.ToString());
        }

        [Test]
        public void EnumsAndSelfReferencingStructuresDoNotThrow()
        {
            var cyclic = new List<object>();
            cyclic.Add(cyclic);
            var w = new JsonWriter();
            Assert.DoesNotThrow(() => w.Value((object)cyclic));
            w.Clear();
            w.Value((object)UnityEngine.LogType.Warning);
            Assert.AreEqual("\"Warning\"", w.ToString());
        }
    }
}
