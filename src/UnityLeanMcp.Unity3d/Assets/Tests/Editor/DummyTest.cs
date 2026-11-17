using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Tests
{
    public interface IOperation
    {
        Task<bool> ExecuteAsync();
    }

    public interface IProcessor
    {
        Task<bool> RunAsync();
    }

    public sealed class CompletedOperation : IOperation
    {
        public Task<bool> ExecuteAsync() => Task.FromResult(true);
    }

    public sealed class Processor : IProcessor
    {
        private readonly IOperation _operation;

        public Processor(IOperation operation) => _operation = operation;

        public async Task<bool> RunAsync()
        {
            bool result = await _operation.ExecuteAsync();
            return result;
        }
    }

    public class DummyTest
    {
        [Test]
        public void PassTest()
        {
            Assert.Pass();
        }

        [Test]
        public void SpecificTargetTest()
        {
            IProcessor processor = new Processor(new CompletedOperation());
            bool result = processor.RunAsync().GetAwaiter().GetResult();
            Assert.That(result, Is.True);
        }

        [Test]
        public void OtherTest()
        {
            Assert.Pass();
        }

        [Test]
        public void NormalTest()
        {
            Assert.Pass();
        }

        [Test]
        [Category("LongRunning")]
        public void LongTest()
        {
            Assert.Pass();
        }

        [Test]
        [Category("Failing")]
        [Explicit("Explicit failing test for failure diagnostic testing")]
        public void FailTest()
        {
            Assert.Fail("This test failed intentionally.");
        }

        [Test]
        [Ignore("This test is skipped intentionally.")]
        public void IgnoreTest()
        {
            Assert.Pass();
        }

        [Test]
        public void PassWithWarningTest()
        {
            // CS0219: Variable is assigned but its value is never used
            int unusedVar = 42;
            Assert.Pass();
        }
    }
}
