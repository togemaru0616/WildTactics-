using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

[RequireComponent(typeof(Camera))]
public class GameCamera : MonoBehaviour
{
    public int TilesWide = 8;

    float _uiBottom = 0f;
    float _uiTop    = 0f;

    public void Init(float uiBottom, float uiTop) { _uiBottom = uiBottom; _uiTop = uiTop; }

    const float DragThreshold = 15f;
    const float ScrollZoomSpeed = 1.5f;
    const float PinchZoomSpeed  = 0.02f;

    float _minX, _maxX, _minZ, _maxZ;
    float _boardMarginX, _boardMarginZ;
    float _totalW, _totalH, _spacing, _aspect;
    float _minOrtho, _maxOrtho;
    Vector2 _touchStart;
    bool _scrolling;
    float _prevPinchDist = -1f;
    Camera _cam;
    bool _flip; // P2 は盤面を180度反転して見る

    public float OrthoSize { get; private set; }

    void OnEnable()  => EnhancedTouchSupport.Enable();
    void OnDisable() => EnhancedTouchSupport.Disable();

    void Start()
    {
        _cam = GetComponent<Camera>();
        _cam.orthographic = true;
        _cam.allowHDR = true;
        SetupBloom();
        if (FindAnyObjectByType<AudioListener>() == null)
            gameObject.AddComponent<AudioListener>();
        _flip = OnlineManager.IsOnline && !OnlineManager.IsHost;
        transform.rotation = Quaternion.Euler(90f, _flip ? 180f : 0f, 0f);

        _spacing = GridManager.Instance.TileSize + GridManager.Instance.TileGap;
        _aspect  = (float)Screen.width / Screen.height;

        OrthoSize = TilesWide * _spacing / (2f * _aspect);

        _totalW = (TerrainGenerator.COLS - 1) * _spacing;
        _totalH = (TerrainGenerator.ROWS - 1) * _spacing;
        // フレーム外縁（WoodenBase 1.14倍の増分 = 各辺7%）までカメラが動けるよう
        // GridManager が作ったボードの実座標をそのまま使う
        _boardMarginX = -GridManager.BoardLeft;
        _boardMarginZ = -GridManager.BoardNear;

        float boardW = GridManager.BoardRight - GridManager.BoardLeft;
        float boardH = GridManager.BoardFar   - GridManager.BoardNear;
        _minOrtho = 3f * _spacing / _aspect;
        _maxOrtho = Mathf.Min(boardW / (2f * _aspect), boardH / 2f);

        if (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Placement)
        {
            _cam.cullingMask = 0;
            transform.position = new Vector3(_totalW / 2f, 20f, _totalH / 2f);
            return;
        }

        _cam.orthographicSize = Mathf.Clamp(OrthoSize, _minOrtho, _maxOrtho);
        OrthoSize = _cam.orthographicSize;
        RecalcBounds();
        transform.position = new Vector3(_totalW / 2f, 20f, _totalH / 2f);
    }

    // orthoSize が変わるたびに位置の clamp 範囲を再計算
    void RecalcBounds()
    {
        float halfW = _cam.orthographicSize * _aspect;

        _minX = halfW - _boardMarginX;
        _maxX = _totalW - halfW + _boardMarginX;
        if (_maxX < _minX) _minX = _maxX = _totalW / 2f;

        _minZ = _cam.orthographicSize * (1f - 2f * _uiBottom) - _boardMarginZ;
        _maxZ = _totalH - _cam.orthographicSize * (1f - 2f * _uiTop) + _boardMarginZ;
        if (_maxZ < _minZ) _minZ = _maxZ = _totalH / 2f;

        // 位置もはみ出さないよう clamp
        var p = transform.position;
        p.x = Mathf.Clamp(p.x, _minX, _maxX);
        p.z = Mathf.Clamp(p.z, _minZ, _maxZ);
        transform.position = p;
    }

    void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Placement) return;
        if (_isZooming) return;

        float dx = 0f, dz = 0f;

        int touchCount = Touch.activeTouches.Count;

        if (touchCount == 2)
        {
            // 2本指ピンチズーム
            var t0 = Touch.activeTouches[0];
            var t1 = Touch.activeTouches[1];
            float dist = Vector2.Distance(t0.screenPosition, t1.screenPosition);

            if (_prevPinchDist > 0f)
            {
                float delta = dist - _prevPinchDist;  // 正=開く=ズームイン
                ApplyZoom(-delta * PinchZoomSpeed);
            }
            _prevPinchDist = dist;
            _scrolling = false;
        }
        else
        {
            _prevPinchDist = -1f;

            if (touchCount == 1)
            {
                var t = Touch.activeTouches[0];
                if (t.began)  { _touchStart = t.screenPosition; _scrolling = false; }
                if (!t.began && !t.ended)
                {
                    if (!_scrolling && Vector2.Distance(t.screenPosition, _touchStart) > DragThreshold) _scrolling = true;
                    float panSign = _flip ? 1f : -1f;
                    if (_scrolling) { dx = panSign * t.delta.x * 0.015f; dz = panSign * t.delta.y * 0.015f; }
                }
                if (t.ended) _scrolling = false;
            }
            else
            {
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    if (mouse.leftButton.wasPressedThisFrame)  { _touchStart = mouse.position.ReadValue(); _scrolling = false; }
                    if (mouse.leftButton.isPressed)
                    {
                        if (!_scrolling && Vector2.Distance(mouse.position.ReadValue(), _touchStart) > DragThreshold) _scrolling = true;
                        if (_scrolling)
                        {
                            float panSign = _flip ? 1f : -1f;
                            var d = mouse.delta.ReadValue();
                            dx = panSign * d.x * 0.012f;
                            dz = panSign * d.y * 0.012f;
                        }
                    }
                    if (mouse.leftButton.wasReleasedThisFrame) _scrolling = false;

                    // マウスホイールズーム（上スクロール=ズームイン）
                    float scroll = mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(scroll) > 0.01f)
                        ApplyZoom(-scroll * ScrollZoomSpeed);
                }
            }
        }

        if (Mathf.Abs(dx) > 0.001f || Mathf.Abs(dz) > 0.001f)
        {
            var p = transform.position;
            p.x = Mathf.Clamp(p.x + dx, _minX, _maxX);
            p.z = Mathf.Clamp(p.z + dz, _minZ, _maxZ);
            transform.position = p;
        }
    }

    void ApplyZoom(float delta)
    {
        float newSize = Mathf.Clamp(_cam.orthographicSize + delta, _minOrtho, _maxOrtho);
        if (Mathf.Approximately(newSize, _cam.orthographicSize)) return;
        _cam.orthographicSize = newSize;
        OrthoSize = newSize;
        RecalcBounds();
    }

    void SetupBloom()
    {
        var urpData = _cam.GetUniversalAdditionalCameraData();
        if (urpData != null) urpData.renderPostProcessing = true;

        var volGo = new GameObject("PostProcessVolume");
        volGo.hideFlags = HideFlags.HideAndDontSave;
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.hideFlags = HideFlags.HideAndDontSave;
        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.5f;
        bloom.threshold.overrideState = true;
        bloom.threshold.value = 0.8f;

        vol.profile = profile;
    }

    public bool IsScrolling => _scrolling;

    bool _isZooming;

    public IEnumerator ZoomTo(Vector3 worldPos, float targetOrtho, float duration)
    {
        _isZooming = true;
        float startOrtho  = _cam.orthographicSize;
        Vector3 startPos  = transform.position;
        float clampedOrtho = Mathf.Clamp(targetOrtho, _minOrtho, _maxOrtho);
        Vector3 targetPos = new Vector3(
            Mathf.Clamp(worldPos.x, _minX, _maxX),
            transform.position.y,
            Mathf.Clamp(worldPos.z, _minZ, _maxZ));

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Min(elapsed / duration, 1f));
            _cam.orthographicSize = Mathf.Lerp(startOrtho, clampedOrtho, t);
            OrthoSize = _cam.orthographicSize;
            transform.position = Vector3.Lerp(startPos, targetPos, t);
            RecalcBounds();
            yield return null;
        }
        _isZooming = false;
    }
}
