using UnityEngine;
using System;

[DefaultExecutionOrder(-200)]
public class LogFilter : MonoBehaviour
{
    void Awake() =>
        Debug.unityLogger.logHandler = new FilteredLogHandler(Debug.unityLogger.logHandler);

    class FilteredLogHandler : ILogHandler
    {
        readonly ILogHandler _inner;
        public FilteredLogHandler(ILogHandler inner) => _inner = inner;

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            // リリースビルドでは Debug.Log を抑制（Warning / Error は常に出力）
            if (!Debug.isDebugBuild && logType == LogType.Log) return;

            if (format != null && (format.Contains("JobTempAlloc")
                                || format.Contains("permitted lifetime of 4 frames"))) return;
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context) =>
            _inner.LogException(exception, context);
    }
}
