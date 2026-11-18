using UnityEditor;
using UnityEditor.Compilation;

namespace UnityLeanMcp
{
    internal sealed class RecompileHandler : CompilationCommandHandlerBase
    {
        protected override string OperationKind => OperationKinds.Recompile;
        protected override string StatusWord => "RECOMPILING";
        protected override void ExecuteCompilation()
        {
            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        }
    }
}
