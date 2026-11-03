using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using WebViewToolkit;

public sealed class MonacoPlayerSmokeRunner : MonoBehaviour
{
    private int systemSourceCount = -1;

    private IEnumerator Start()
    {
        CodeBrowserWrapper wrapper = GetComponentInChildren<CodeBrowserWrapper>(true);
        if (wrapper == null)
        {
            Fail("CodeBrowserWrapper not found in the smoke scene.");
            yield break;
        }

        ICodeBrowser browser = wrapper.GetCodeBrowser();
        MonacoCodeBrowser monaco = browser as MonacoCodeBrowser;
        if (monaco == null)
        {
            Fail("CodeBrowserWrapper did not create MonacoCodeBrowser.");
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + 30f;
        while (!monaco.IsAvailable && Time.realtimeSinceStartup < deadline) yield return null;
        if (!monaco.IsAvailable)
        {
            Fail("Monaco page did not become available.");
            yield break;
        }

        const string expectedCode = "// free Monaco smoke\nconst smokeValue = 42;";
        browser.SetCode(expectedCode);
        browser.SetReadOnly(true);
        browser.SetReadOnly(false);
        browser.SetScrollTop(0);
        browser.SetZoom(2f);
        browser.AddError(1, "Smoke diagnostic");
        browser.ClearErrors();
        monaco.AddSystemSource("declare const smokeSystemApi: number;", "smoke-system-api.d.ts");

        FieldInfo webViewField = typeof(MonacoCodeBrowser).GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic);
        WebViewInstance webView = webViewField != null ? webViewField.GetValue(monaco) as WebViewInstance : null;
        if (webView == null)
        {
            Fail("Monaco WebView instance not found after page initialization.");
            yield break;
        }

        webView.MessageReceived += OnSmokeMessage;
        if (!webView.ExecuteScript("window.chrome.webview.postMessage(JSON.stringify({type:'smoke',value:String(Object.keys(monaco.languages.typescript.javascriptDefaults.getExtraLibs()).length)}));"))
        {
            Fail("Could not query Monaco system typings.");
            yield break;
        }

        float bridgeDeadline = Time.realtimeSinceStartup + 10f;
        while (systemSourceCount < 0 && Time.realtimeSinceStartup < bridgeDeadline) yield return null;
        if (systemSourceCount < 2)
        {
            Fail("Monaco did not load the added system typings.");
            yield break;
        }

        yield return new WaitForSecondsRealtime(1f);
        string actualCode = null;
        browser.GetCodeAsync(value => actualCode = value);
        if (!string.Equals(actualCode, expectedCode, StringComparison.Ordinal))
        {
            Fail("Code round trip failed: " + (actualCode ?? "<null>"));
            yield break;
        }

        ScreenCapture.CaptureScreenshot(@"C:\Users\rafael\AppData\Local\Temp\GameBuilderMonacoSmoke-20261103\MonacoSmoke.png");
        Debug.Log("MONACO_SMOKE_PASS|ready|code-roundtrip|system-typings=" + systemSourceCount);
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit(0);
    }

    private void OnSmokeMessage(string message)
    {
        SmokeMessage payload = JsonUtility.FromJson<SmokeMessage>(message);
        if (payload != null && payload.type == "smoke")
        {
            int.TryParse(payload.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out systemSourceCount);
        }
    }

    private void Fail(string reason)
    {
        Debug.LogError("MONACO_SMOKE_FAIL|" + reason);
        Application.Quit(2);
    }

    [Serializable]
    private sealed class SmokeMessage
    {
        public string type;
        public string value;
    }
}
