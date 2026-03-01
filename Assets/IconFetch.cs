using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

[RequireComponent(typeof(KMService))]
public class IconFetch : MonoBehaviour
{
    private const string RepositoryHost = "https://ktane.timwi.de";

    public static IconFetch Instance { get; private set; }

    private KtaneModule[] _modules;

    private Texture2D _iconSprite;

    private bool _isError = false, _fetchRepoDone = false, _fetchIconSpriteDone = false;

    private void Awake()
    {
        // set static instance
        Instance = this;
        // start fetching repo data and iconsprite
        StartCoroutine(FetchRepo());
        StartCoroutine(FetchIconSprite());
    }

    private IEnumerator FetchRepo()
    {
        try
        {
            Log("Fetching repository data");

            // fetch json data
            using (var req = UnityWebRequest.Get(string.Format("{0}/json/raw", RepositoryHost)))
            {
                yield return req.SendWebRequest();
                if (req.isHttpError || req.isNetworkError)
                {
                    _isError = true;
                    Log("Failed to get repository data: {0}", req.error);
                    yield break;
                }

                // deserialize json data
                _modules = JsonConvert.DeserializeObject<KtaneModuleResult>(req.downloadHandler.text).KtaneModules;
                Log("Got {0} modules", _modules.Length);
            }
        }
        finally
        {
            _fetchRepoDone = true;
        }
    }

    private IEnumerator FetchIconSprite()
    {
        try
        {
            Log("Fetching iconsprite");

            // fetch sprite
            using (var req = UnityWebRequestTexture.GetTexture(string.Format("{0}/iconsprite", RepositoryHost)))
            {
                yield return req.SendWebRequest();
                if (req.isHttpError || req.isNetworkError)
                {
                    _isError = true;
                    Log("Failed to get iconsprite: {0}", req.error);
                    yield break;
                }

                // get sprite
                _iconSprite = DownloadHandlerTexture.GetContent(req);
                Log("Got iconsprite ({0}x{1})", _iconSprite.width, _iconSprite.height);
            }
        }
        finally
        {
            _fetchIconSpriteDone = true;
        }
    }

    public void WaitForFetch(Action<bool> callback)
    {
        StartCoroutine(WaitForFetchRoutine(callback));
    }

    private IEnumerator WaitForFetchRoutine(Action<bool> callback)
    {
        // make sure both routines are done
        // if they've already finished, callback is invoked instantly
        yield return new WaitUntil(() => _fetchRepoDone && _fetchIconSpriteDone);
        callback.Invoke(_isError);
    }

    public Texture2D GetIcon(string moduleId)
    {
        // find module data
        var module = _modules.First(mod => mod.ModuleId == moduleId);

        // capture those pixels from the iconsprite
        var pixels = _iconSprite.GetPixels(32 * module.IconX, _iconSprite.height - 32 * (module.IconY + 1), 32, 32);

        // create a new texture with those pixels
        var res = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        res.SetPixels(pixels);
        res.Apply();

        return res;
    }

    private void Log(string message, params object[] args)
    {
        Debug.LogFormat("[IconFetch] {0}", string.Format(message, args));
    }
}
