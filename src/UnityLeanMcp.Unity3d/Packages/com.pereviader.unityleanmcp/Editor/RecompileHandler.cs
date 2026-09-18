using UnityEditor.Compilation;
using UnityLeanMcp;

namespace PereViader.UnityLeanMcp.Editor
{
    internal sealed class RecompileHandler : CompilationCommandHandlerBase
    {
        protected override string OperationKind => OperationKinds.Recompile;
        protected override string StatusWord => "RECOMPILING";
        protected override void ExecuteCompilation() => CompilationPipeline.RequestScriptCompilation();
    }
}
