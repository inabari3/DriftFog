using UnityEngine;
using UnityEngine.UIElements;

namespace DriftFog.Demo
{
    /// <summary>中庭を巡回するランタンを動かし、実行中の霧の品質を切り替えます。</summary>
    public sealed class CourtyardControls : MonoBehaviour
    {
        [SerializeField] private Camera _view;
        [SerializeField] private Transform _lantern;
        [SerializeField] private DriftFogVolume _fog;
        [SerializeField] private PanelSettings _panelSettings;
        [SerializeField] private VisualTreeAsset _layout;
        private FogProfile _profile;
        private Vector3 _destination;
        private bool _automatic = true;
        private bool _showControls = true;
        private float _phase;
        private VisualElement _screen;
        private VisualElement _controls;
        private Button _route;
        private Button _quality;
        private Button _simulation;
        private Button _wind;
        private Button _cameraView;
        private Vector3 _widePosition;
        private Quaternion _wideRotation;
        private float _wideFov;
        private Vector3 _followPoint;
        private bool _follow;
        private Button _show;
        private Vector3 _courtyardWind;
        private float _courtyardTurbulence;
        private bool _steadyWind;
        private bool _displayedFluid;

        /// <summary>シーン生成時に操作対象と画面表示の参照を設定します。</summary>
        /// <param name="view">地面を指すカメラです。</param>
        /// <param name="lantern">霧を押し分けるランタンです。</param>
        /// <param name="fog">サンプルで品質を切り替えるボリュームです。</param>
        /// <param name="panelSettings">入力と表示サイズを制御するパネル設定です。</param>
        /// <param name="layout">操作画面のレイアウトです。</param>
        public void Configure(Camera view, Transform lantern, DriftFogVolume fog, PanelSettings panelSettings, VisualTreeAsset layout)
        {
            _view = view;
            _lantern = lantern;
            _fog = fog;
            _panelSettings = panelSettings;
            _layout = layout;
        }

        private void Start()
        {
            if (_fog != null && _fog.Profile != null)
            {
                _profile = Instantiate(_fog.Profile);
                _fog.Profile = _profile;
                _courtyardWind = _profile.Wind;
                _courtyardTurbulence = _profile.Turbulence;
            }

            if (_panelSettings == null || _layout == null)
            {
                return;
            }

            GameObject overlay = new GameObject("Courtyard HUD");
            overlay.SetActive(false);
            overlay.transform.SetParent(transform, false);
            UIDocument document = overlay.AddComponent<UIDocument>();
            document.panelSettings = _panelSettings;
            document.visualTreeAsset = _layout;
            overlay.SetActive(true);
            VisualElement root = document.rootVisualElement;
            root.style.flexGrow = 1f;
            _screen = root.Q("courtyard-screen");
            _controls = root.Q("controls");
            _route = root.Q<Button>("route");
            _quality = root.Q<Button>("quality");
            _simulation = root.Q<Button>("simulation");
            _wind = root.Q<Button>("wind");
            _cameraView = root.Q<Button>("camera-view");
            _show = root.Q<Button>("show-controls");
            root.Q<Button>("hide-controls").clicked += ToggleControls;
            _show.clicked += ToggleControls;
            _route.clicked += ToggleRoute;
            _quality.clicked += CycleQuality;
            _quality.SetEnabled(_profile != null);
            if (_simulation != null)
            {
                _simulation.SetEnabled(_profile != null && SystemInfo.supportsComputeShaders);
                _simulation.clicked += ToggleSimulation;
                UpdateSimulationLabel();
            }

            if (_wind != null)
            {
                _wind.SetEnabled(_profile != null);
                _wind.clicked += ToggleWind;
            }

            if (_cameraView != null && _view && _lantern)
            {
                _widePosition = _view.transform.position;
                _wideRotation = _view.transform.rotation;
                _wideFov = _view.fieldOfView;
                _cameraView.clicked += ToggleView;
                ToggleView();
            }

            UpdateQualityLabel();
            _screen.RegisterCallback<PointerDownEvent>(OnPointer);
            _screen.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            _screen.Focus();
        }

        private void OnDestroy()
        {
            if (_profile != null)
            {
                Destroy(_profile);
            }
        }

        private void Update()
        {
            if (_simulation != null && _fog && _displayedFluid != (bool)_fog.FluidDensity)
            {
                UpdateSimulationLabel();
            }

            if (_lantern == null)
            {
                return;
            }

            _phase += Time.deltaTime * 0.43f;
            if (_automatic)
            {
                _destination = new Vector3(4.8f * Mathf.Cos(_phase), 0f, -1.2f + 2.4f * Mathf.Sin(2f * _phase));
            }

            _destination.y = 0.88f + 0.12f * Mathf.Sin(4f * _phase);
            _lantern.position = Vector3.MoveTowards(_lantern.position, _destination, Time.deltaTime * 4.8f);
            _lantern.rotation = Quaternion.Euler(0f, _phase * 27f, 0f);
            if (_follow)
            {
                _followPoint = Vector3.Lerp(_followPoint, _lantern.position, 1f - Mathf.Exp(-5f * Time.deltaTime));
                FrameFlow(_view, _followPoint);
            }
        }

        /// <summary>地上の対象と周囲の巻き返しが同時に見える斜め上方へカメラを配置する。</summary>
        /// <param name="view">撮影するカメラ。</param>
        /// <param name="subject">追尾する対象のワールド座標。</param>
        public static void FrameFlow(Camera view, Vector3 subject)
        {
            Vector3 center = new Vector3(subject.x, 0.55f, subject.z);
            Vector3 offset = new Vector3(0.55f, 5.45f, -2.4f);
            view.transform.SetPositionAndRotation(center + offset, Quaternion.LookRotation(-offset, Vector3.up));
            view.fieldOfView = 44f;
        }

        private void ToggleView()
        {
            _follow = !_follow;
            if (_follow)
            {
                _followPoint = _lantern.position;
                FrameFlow(_view, _followPoint);
            }
            else
            {
                _view.transform.SetPositionAndRotation(_widePosition, _wideRotation);
                _view.fieldOfView = _wideFov;
            }

            _cameraView.text = _follow ? "View: follow" : "View: courtyard";
        }

        private void OnPointer(PointerDownEvent input)
        {
            if (input.button != 0 || input.target != _screen || _view == null)
            {
                return;
            }

            // パネル単位を正規化すると、端末の DPI や UI の拡大率に依存せず地面を選べる。
            Rect bounds = _screen.worldBound;
            Vector2 uv = Rect.PointToNormalized(bounds, input.position);
            Ray ray = _view.ScreenPointToRay(new Vector3(uv.x * Screen.width, (1f - uv.y) * Screen.height));
            Plane ground = new Plane(Vector3.up, Vector3.zero);
            if (ground.Raycast(ray, out float distance))
            {
                Vector3 hit = ray.GetPoint(distance);
                _destination = new Vector3(Mathf.Clamp(hit.x, -7f, 7f), 0f, Mathf.Clamp(hit.z, -6f, 3.5f));
                _automatic = false;
                _route.text = "Resume route";
            }

            _screen.Focus();
        }

        private void OnKey(KeyDownEvent input)
        {
            if (input.keyCode == KeyCode.H)
            {
                ToggleControls();
                input.StopPropagation();
            }
        }

        private void ToggleControls()
        {
            _showControls = !_showControls;
            _controls.style.display = _showControls ? DisplayStyle.Flex : DisplayStyle.None;
            _show.style.display = _showControls ? DisplayStyle.None : DisplayStyle.Flex;
            _screen.Focus();
        }

        private void ToggleRoute()
        {
            _destination = _lantern != null ? _lantern.position : Vector3.zero;
            _automatic = !_automatic;
            _route.text = _automatic ? "Pause route" : "Resume route";
        }

        private void CycleQuality()
        {
            switch (_profile.Quality)
            {
                case FogQuality.Low:
                    _profile.Quality = FogQuality.Balanced;
                    break;
                case FogQuality.Balanced:
                    _profile.Quality = FogQuality.High;
                    break;
                default:
                    _profile.Quality = FogQuality.Low;
                    break;
            }

            _fog.Refresh();
            UpdateQualityLabel();
        }

        private void ToggleSimulation()
        {
            _profile.SimulateFluid = !_profile.SimulateFluid;
            _fog.Refresh();
            UpdateSimulationLabel();
        }

        private void UpdateSimulationLabel()
        {
            _displayedFluid = _fog && _fog.FluidDensity;
            _simulation.text = _displayedFluid ? "Flow: simulated" : (_profile && _profile.SimulateFluid ? "Flow: auto (analytic)" : "Flow: analytic");
        }

        private void ToggleWind()
        {
            // 柱からカメラ側に流す。ランタンの巡回と手動操作はそのまま比較に使える。
            _steadyWind = !_steadyWind;
            _profile.Wind = _steadyWind ? new Vector3(0f, 0f, -2.5f) : _courtyardWind;
            _profile.Turbulence = _steadyWind ? 0f : _courtyardTurbulence;
            _wind.text = _steadyWind ? "Wind: steady" : "Wind: courtyard";
            _fog.Refresh();
        }

        private void UpdateQualityLabel()
        {
            if (_profile != null)
            {
                _quality.text = "Quality: " + _profile.Quality.ToString().ToLowerInvariant();
            }
        }
    }
}
