using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class UnityResultFormatterTests
    {
        private int m_MaxDepth;
        private int m_MaxItems;
        private int m_MaxOutputCharacters;
        private int m_MaxOutputBytes;

        [SetUp]
        public void CaptureFormatterLimits()
        {
            m_MaxDepth = UnityResultFormatter.MaxDepth;
            m_MaxItems = UnityResultFormatter.MaxItems;
            m_MaxOutputCharacters = UnityResultFormatter.MaxOutputCharacters;
            m_MaxOutputBytes = UnityResultFormatter.MaxOutputBytes;
        }

        [TearDown]
        public void RestoreFormatterLimits()
        {
            UnityResultFormatter.MaxDepth = m_MaxDepth;
            UnityResultFormatter.MaxItems = m_MaxItems;
            UnityResultFormatter.MaxOutputCharacters = m_MaxOutputCharacters;
            UnityResultFormatter.MaxOutputBytes = m_MaxOutputBytes;
        }

        [Test]
        public void FormatResult_SelfReferencingEnumerable_ReturnsCycleMarker()
        {
            var values = new List<object>();
            values.Add(values);

            string formatted = UnityResultFormatter.FormatResult(values, prettyPrint: false);

            StringAssert.Contains("truncated: cycle detected", formatted);
        }

        [Test]
        public void FormatResult_DeeplyNestedEnumerable_ReturnsDepthMarker()
        {
            UnityResultFormatter.MaxDepth = 2;
            object value = "leaf";
            for (int i = 0; i < 8; i++)
            {
                value = new List<object> { value };
            }

            string formatted = UnityResultFormatter.FormatResult(value, prettyPrint: false);

            StringAssert.Contains("truncated: maximum depth reached", formatted);
        }

        [Test]
        public void FormatResult_DeeplyNestedJsonObject_ReturnsDepthMarker()
        {
            UnityResultFormatter.MaxDepth = 2;
            var value = new JsonNode { Value = "leaf" };
            for (int i = 0; i < 8; i++)
            {
                value = new JsonNode { Child = value };
            }

            string formatted = UnityResultFormatter.FormatResult(value, prettyPrint: false);

            StringAssert.Contains("truncated: maximum depth reached", formatted);
        }

        [Test]
        public void FormatResult_CyclicJsonObject_ReturnsCycleMarker()
        {
            var value = new JsonNode();
            value.Child = value;

            string formatted = UnityResultFormatter.FormatResult(value, prettyPrint: false);

            StringAssert.Contains("truncated: cycle detected", formatted);
        }

        [Test]
        public void FormatResult_CustomInfiniteEnumerable_StopsAtItemLimit()
        {
            UnityResultFormatter.MaxItems = 3;

            string formatted = UnityResultFormatter.FormatResult(new InfiniteEnumerable(), prettyPrint: false);

            StringAssert.Contains("truncated: maximum item count reached", formatted);
        }

        [Test]
        public void FormatResult_FiniteEnumerable_FormatsAllItemsAtLimit()
        {
            UnityResultFormatter.MaxItems = 3;

            string formatted = UnityResultFormatter.FormatResult(
                new List<int> { 1, 2, 3 },
                prettyPrint: false);

            Assert.That(formatted, Is.EqualTo("[1, 2, 3]"));
        }

        [Test]
        public void FormatResult_Vector3_FormatsWithInvariantCultureF2()
        {
            var v = new Vector3(1.234f, 5.678f, -9.012f);
            string formatted = UnityResultFormatter.FormatResult(v, prettyPrint: false);
            Assert.That(formatted, Is.EqualTo("(1.23, 5.68, -9.01)"));
        }

        [Test]
        public void FormatResult_Color_FormatsWithInvariantCultureF3()
        {
            var c = new Color(1f, 0.5f, 0.25f, 0.8f);
            string formatted = UnityResultFormatter.FormatResult(c, prettyPrint: false);
            Assert.That(formatted, Is.EqualTo("RGBA(1.000, 0.500, 0.250, 0.800)"));
        }

        [Test]
        public void FormatResult_Bounds_FormatsCenterAndExtents()
        {
            var b = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(4f, 6f, 8f));
            string formatted = UnityResultFormatter.FormatResult(b, prettyPrint: false);
            Assert.That(formatted, Is.EqualTo("Center: (1.00, 2.00, 3.00), Extents: (2.00, 3.00, 4.00)"));
        }

        [Test]
        public void FormatResult_Rect_FormatsCoordinatesAndSize()
        {
            var r = new Rect(10f, 20f, 100f, 200f);
            string formatted = UnityResultFormatter.FormatResult(r, prettyPrint: false);
            Assert.That(formatted, Is.EqualTo("(x:10.00, y:20.00, width:100.00, height:200.00)"));
        }

        [Test]
        public void FormatResult_Vector3Array_FormatsThroughEnumerableFormatter()
        {
            var array = new[] { new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f) };
            string formatted = UnityResultFormatter.FormatResult(array, prettyPrint: false);
            Assert.That(formatted, Is.EqualTo("[(1.00, 2.00, 3.00), (4.00, 5.00, 6.00)]"));
        }

        [Test]
        public void DefaultFormatters_ContainsUnityMathFormatterWithPriority35()
        {
            UnityResultFormatter.ResetToDefaults();
            var formatters = UnityResultFormatter.Formatters;
            var mathFormatter = formatters.OfType<UnityMathFormatter>().FirstOrDefault();
            Assert.That(mathFormatter, Is.Not.Null);
            Assert.That(mathFormatter.Priority, Is.EqualTo(35));
        }

        [Test]
        public void FormatResult_OversizedString_RespectsCharacterAndByteLimits()
        {
            UnityResultFormatter.MaxOutputCharacters = 64;
            UnityResultFormatter.MaxOutputBytes = 128;

            string formatted = UnityResultFormatter.FormatResult(new string('x', 10_000), prettyPrint: false);

            StringAssert.Contains("truncated: maximum output size reached", formatted);
            Assert.That(formatted.Length, Is.LessThanOrEqualTo(64));
            Assert.That(Encoding.UTF8.GetByteCount(formatted), Is.LessThanOrEqualTo(128));
        }

        [Test]
        public void FormatResult_OversizedJson_RespectsOutputLimits()
        {
            UnityResultFormatter.MaxOutputCharacters = 80;
            UnityResultFormatter.MaxOutputBytes = 160;

            string formatted = UnityResultFormatter.FormatResult(
                new JsonPayload { Text = new string('y', 10_000) },
                prettyPrint: false);

            StringAssert.Contains("truncated: maximum output size reached", formatted);
            Assert.That(formatted.Length, Is.LessThanOrEqualTo(80));
            Assert.That(Encoding.UTF8.GetByteCount(formatted), Is.LessThanOrEqualTo(160));
        }

        [Test]
        public void FormatResult_AggregateOversizedJson_RefusesBeforeSerialization()
        {
            UnityResultFormatter.MaxOutputCharacters = 80;
            UnityResultFormatter.MaxOutputBytes = 160;

            string formatted = UnityResultFormatter.FormatResult(
                new WideJsonPayload { Values = new[]
                {
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x",
                    "x", "x", "x", "x", "x", "x", "x", "x", "x", "x"
                } },
                prettyPrint: false);

            Assert.That(formatted, Is.EqualTo("... (truncated: maximum output size reached)"));
        }

        private sealed class InfiniteEnumerable : IEnumerable
        {
            public IEnumerator GetEnumerator()
            {
                while (true)
                {
                    yield return "item";
                }
            }
        }

        [System.Serializable]
        private sealed class JsonPayload
        {
            public string Text;
        }

        [System.Serializable]
        private sealed class WideJsonPayload
        {
            public string[] Values;
        }

        [System.Serializable]
        private sealed class JsonNode
        {
            public string Value;
            public JsonNode Child;
        }
    }
}
