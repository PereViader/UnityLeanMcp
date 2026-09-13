using System;

namespace UnityLeanMcp
{
    public interface IUnityTypeFormatter
    {
        int Priority { get; }
        bool CanFormat(object value);
        string Format(object value, Func<object, string> formatChild, bool prettyPrint = true);
    }
}
