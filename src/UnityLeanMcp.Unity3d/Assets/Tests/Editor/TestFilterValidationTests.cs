using System;
using System.Reflection;
using NUnit.Framework;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class TestFilterValidationTests
    {
        private static bool Validate(string name, string[] values, out string error)
        {
            var method = typeof(RunTestsHandler).GetMethod("TryValidateFilterValues",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string[]), typeof(string).MakeByRefType() }, null);
            object[] args = { name, values, null };
            bool valid = (bool)method.Invoke(null, args);
            error = (string)args[2];
            return valid;
        }

        [TestCase("[")]
        [TestCase("(")]
        [TestCase("*Movement*")]
        public void InvalidGroupRegexReportsIndexAndParseError(string pattern)
        {
            Assert.That(Validate("groupNames", new[] { "Valid.*", pattern }, out string error), Is.False);
            Assert.That(error, Does.Contain("Invalid test filter 'groupNames[1]'"));
            var parseError = Assert.Catch<ArgumentException>(() => new System.Text.RegularExpressions.Regex(pattern));
            Assert.That(error, Does.Contain(parseError.Message));
        }

        [TestCase("testNames")]
        [TestCase("categoryNames")]
        [TestCase("assemblyNames")]
        public void LiteralFiltersDoNotRequireValidRegex(string name)
        {
            Assert.That(Validate(name, new[] { "[", "(" }, out _), Is.True);
        }

        [Test]
        public void ValidGroupExpressionsAreAccepted()
        {
            Assert.That(Validate("groupNames", new[] { "^Namespace\\.Fixture", "(?i)(First|Second).*" }, out _), Is.True);
        }
    }
}
