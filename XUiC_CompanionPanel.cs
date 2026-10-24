using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine.Scripting;

namespace WebMap
{
    // The "Web Map" panel on the right side of the map screen (M). Window in XUi/windows.xml.
    // Only runs while the map screen is open.
    [Preserve]
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class XUiC_CompanionPanel : XUiController
    {
        private const float RefreshInterval = 1f;

        private XUiC_ComboBoxList<string> cbxFollow;
        private XUiC_ComboBoxList<string> cbxZoom;
        private XUiC_ComboBoxList<string> cbxTier;
        private XUiC_ToggleButton togglePois;
        private readonly XUiC_ToggleButton[] categoryToggles = new XUiC_ToggleButton[CompanionSettings.Categories.Length];
        private XUiV_Label lblStatus;
        private readonly XUiC_ComboBoxList<string>[] sizeBoxes = new XUiC_ComboBoxList<string>[CompanionSettings.SizeKeys.Length];
        private XUiC_ToggleButton toggleWorkstations;
        private XUiC_ToggleButton toggleWorkstationsActive;
        private XUiC_ComboBoxList<string> cbxWorkstationSide;
        private readonly XUiC_ToggleButton[] workstationToggles = new XUiC_ToggleButton[WorkstationTypes.Length];

        // Pages: the usual settings, workstations, and text/icon sizes; the buttons at the bottom switch between them
        private const int PageBasic = 0, PageWorkstations = 1, PageAdvanced = 2;
        private XUiView pageBasic;
        private XUiView pageWorkstations;
        private XUiView pageAdvanced;
        private XUiC_SimpleButton btnWorkstations;
        private XUiC_SimpleButton btnAdvanced;
        private Vector2i advancedPos;  // its place in windows.xml; as Back it moves to where Workstations is
        private int page;

        // Workstation types with a toggle (ws_type_<type> in windows.xml)
        private static readonly string[] WorkstationTypes =
            { "campfire", "forge", "workbench", "combineStation", "cementMixer", "chemistryStation", "tableSaw", CompanionSettings.OtherWorkstation };

        // Follow choices shown in cbxFollow: CompanionSettings follow values, same order
        private readonly List<string> followKeys = new List<string>();
        private readonly List<string> followNames = new List<string>();
        private readonly List<string> scratchKeys = new List<string>();
        private readonly List<string> scratchNames = new List<string>();

        private int seenVersion = -1;
        private float nextRefresh;

        public override void Init()
        {
            base.Init();
            cbxFollow = GetChildById("cbxFollow") as XUiC_ComboBoxList<string>;
            if (cbxFollow != null) cbxFollow.OnValueChanged += OnFollowChanged;

            cbxZoom = GetChildById("cbxZoom") as XUiC_ComboBoxList<string>;
            if (cbxZoom != null)
            {
                cbxZoom.Elements.Clear();
                for (int z = 0; z < CompanionSettings.ZoomSteps.Length; z++) cbxZoom.Elements.Add(CompanionSettings.ZoomName(z));
                cbxZoom.OnValueChanged += (_sender, _old, _new) => CompanionSettings.SetZoom(cbxZoom.SelectedIndex);
            }

            cbxTier = GetChildById("cbxTier") as XUiC_ComboBoxList<string>;
            if (cbxTier != null)
            {
                cbxTier.Elements.Clear();
                cbxTier.Elements.Add("All");
                for (int t = 1; t <= CompanionSettings.MaxTier; t++) cbxTier.Elements.Add("Tier " + t + "+");
                cbxTier.OnValueChanged += (_sender, _old, _new) => CompanionSettings.SetPoiMinTier(cbxTier.SelectedIndex);
            }

            togglePois = GetChildById("togglePois") as XUiC_ToggleButton;
            if (togglePois != null) togglePois.OnValueChanged += (_sender, _value) => CompanionSettings.SetShowPois(_value);

            for (int i = 0; i < categoryToggles.Length; i++)
            {
                XUiC_ToggleButton toggle = GetChildById("show_" + CompanionSettings.Categories[i]) as XUiC_ToggleButton;
                categoryToggles[i] = toggle;
                if (toggle == null) continue;
                string category = CompanionSettings.Categories[i];
                toggle.Label = CompanionSettings.CategoryNames[i];
                toggle.OnValueChanged += (_sender, _value) => CompanionSettings.SetHidden(category, !_value);
            }

            for (int i = 0; i < sizeBoxes.Length; i++)
            {
                XUiC_ComboBoxList<string> box = GetChildById("size_" + CompanionSettings.SizeKeys[i]) as XUiC_ComboBoxList<string>;
                sizeBoxes[i] = box;
                if (box == null) continue;
                box.Elements.Clear();
                foreach (int step in CompanionSettings.SizeSteps) box.Elements.Add(step + "%");
                int index = i;
                box.OnValueChanged += (_sender, _old, _new) =>
                    CompanionSettings.SetSize(index, CompanionSettings.SizeSteps[box.SelectedIndex]);
                XUiController label = GetChildById("sizeLabel_" + CompanionSettings.SizeKeys[i]);
                XUiV_Label lbl = label != null ? label.ViewComponent as XUiV_Label : null;
                if (lbl != null) lbl.Text = CompanionSettings.SizeNames[i];
            }

            toggleWorkstations = GetChildById("ws_show") as XUiC_ToggleButton;
            if (toggleWorkstations != null) toggleWorkstations.OnValueChanged += (_sender, _value) => CompanionSettings.SetShowWorkstations(_value);
            cbxWorkstationSide = GetChildById("ws_side") as XUiC_ComboBoxList<string>;
            if (cbxWorkstationSide != null)
            {
                cbxWorkstationSide.Elements.Clear();
                cbxWorkstationSide.Elements.AddRange(CompanionSettings.WorkstationSideNames);
                cbxWorkstationSide.OnValueChanged += (_sender, _old, _new) => CompanionSettings.SetWorkstationSide(cbxWorkstationSide.SelectedIndex);
            }
            toggleWorkstationsActive = GetChildById("ws_active") as XUiC_ToggleButton;
            if (toggleWorkstationsActive != null)
                toggleWorkstationsActive.OnValueChanged += (_sender, _value) => CompanionSettings.SetWorkstationsActiveOnly(_value);
            for (int i = 0; i < WorkstationTypes.Length; i++)
            {
                XUiC_ToggleButton toggle = GetChildById("ws_type_" + WorkstationTypes[i]) as XUiC_ToggleButton;
                workstationToggles[i] = toggle;
                if (toggle == null) continue;
                string type = WorkstationTypes[i];
                toggle.Label = type == CompanionSettings.OtherWorkstation ? "Other" : Localization.Get(type);
                toggle.OnValueChanged += (_sender, _value) => CompanionSettings.SetWorkstationHidden(type, !_value);
            }

            pageBasic = ViewOf("pageBasic");
            pageWorkstations = ViewOf("pageWorkstations");
            pageAdvanced = ViewOf("pageAdvanced");
            btnWorkstations = GetChildById("btnWorkstations") as XUiC_SimpleButton;
            if (btnWorkstations != null) btnWorkstations.OnPressed += (_sender, _button) => ShowPage(page == PageWorkstations ? PageBasic : PageWorkstations);
            btnAdvanced = GetChildById("btnAdvanced") as XUiC_SimpleButton;
            if (btnAdvanced != null) btnAdvanced.OnPressed += (_sender, _button) => ShowPage(page == PageAdvanced ? PageBasic : PageAdvanced);
            if (btnAdvanced != null) advancedPos = btnAdvanced.ViewComponent.Position;

            XUiController status = GetChildById("status");
            lblStatus = status != null ? status.ViewComponent as XUiV_Label : null;
        }

        public override void OnOpen()
        {
            base.OnOpen();
            seenVersion = -1;
            nextRefresh = 0f;
            ShowPage(PageBasic);
        }

        private XUiView ViewOf(string _id)
        {
            XUiController c = GetChildById(_id);
            return c != null ? c.ViewComponent : null;
        }

        private void ShowPage(int _page)
        {
            page = _page;
            if (pageBasic != null) pageBasic.IsVisible = _page == PageBasic;
            if (pageWorkstations != null) pageWorkstations.IsVisible = _page == PageWorkstations;
            if (pageAdvanced != null) pageAdvanced.IsVisible = _page == PageAdvanced;
            // On a sub-page only its Back button shows, always on the left
            if (btnWorkstations != null)
            {
                btnWorkstations.Text = _page == PageWorkstations ? "Back" : "Workstations";
                btnWorkstations.ViewComponent.IsVisible = _page != PageAdvanced;
            }
            if (btnAdvanced != null)
            {
                btnAdvanced.Text = _page == PageAdvanced ? "Back" : "Text & icon sizes";
                btnAdvanced.ViewComponent.IsVisible = _page != PageWorkstations;
                btnAdvanced.ViewComponent.Position = _page == PageAdvanced && btnWorkstations != null
                    ? btnWorkstations.ViewComponent.Position
                    : advancedPos;
            }
        }

        public override void Update(float _dt)
        {
            base.Update(_dt);
            nextRefresh -= _dt;
            if (nextRefresh <= 0f)
            {
                nextRefresh = RefreshInterval;
                RefreshFollowList();
                RefreshStatus();
            }
            if (seenVersion != CompanionSettings.Version)
            {
                seenVersion = CompanionSettings.Version;
                RefreshFromSettings();
            }
        }

        private void OnFollowChanged(XUiController _sender, string _old, string _new)
        {
            int i = cbxFollow.SelectedIndex;
            if (i >= 0 && i < followKeys.Count) CompanionSettings.SetFollow(followKeys[i]);
        }

        // Me, every other player the map shows a marker for (party members, allies), and Free
        private void RefreshFollowList()
        {
            if (cbxFollow == null) return;
            scratchKeys.Clear();
            scratchNames.Clear();
            scratchKeys.Add(CompanionSettings.FollowSelf);
            scratchNames.Add("Me");

            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            EntityPlayerLocal local = world != null ? world.GetPrimaryPlayer() : null;
            NavObjectManager mgr = NavObjectManager.Instance;
            if (mgr != null && local != null)
            {
                List<NavObject> list = mgr.NavObjectList;
                for (int i = 0; i < list.Count; i++)
                {
                    NavObject nav = list[i];
                    EntityPlayer p = nav != null ? nav.TrackedEntity as EntityPlayer : null;
                    if (p == null || p.entityId == local.entityId || nav.hiddenOnMap || nav.CurrentMapSettings == null) continue;
                    string key = p.entityId.ToString();
                    if (scratchKeys.Contains(key)) continue;
                    scratchKeys.Add(key);
                    scratchNames.Add(p.PlayerDisplayName);
                }
            }

            // Keep a followed player in the list after their marker goes away, so the choice doesn't jump
            string follow = CompanionSettings.Follow;
            if (follow != CompanionSettings.FollowFree && !scratchKeys.Contains(follow))
            {
                int id;
                EntityPlayer gone = int.TryParse(follow, out id) && world != null ? world.GetEntity(id) as EntityPlayer : null;
                scratchKeys.Add(follow);
                scratchNames.Add(gone != null ? gone.PlayerDisplayName : "Player " + follow);
            }
            scratchKeys.Add(CompanionSettings.FollowFree);
            scratchNames.Add("Free (pan with mouse)");

            if (SameList(scratchKeys, followKeys) && SameList(scratchNames, followNames)) return;
            followKeys.Clear();
            followKeys.AddRange(scratchKeys);
            followNames.Clear();
            followNames.AddRange(scratchNames);
            cbxFollow.Elements.Clear();
            cbxFollow.Elements.AddRange(followNames);
            SelectFollow();
        }

        private void RefreshFromSettings()
        {
            SelectFollow();
            for (int i = 0; i < categoryToggles.Length; i++)
            {
                if (categoryToggles[i] != null) categoryToggles[i].Value = !CompanionSettings.IsHidden(CompanionSettings.Categories[i]);
            }
            if (togglePois != null) togglePois.Value = CompanionSettings.ShowPois;
            if (toggleWorkstations != null) toggleWorkstations.Value = CompanionSettings.ShowWorkstations;
            if (toggleWorkstationsActive != null) toggleWorkstationsActive.Value = CompanionSettings.WorkstationsActiveOnly;
            if (cbxWorkstationSide != null) cbxWorkstationSide.SelectedIndex = CompanionSettings.WorkstationSide;
            for (int i = 0; i < workstationToggles.Length; i++)
            {
                if (workstationToggles[i] != null) workstationToggles[i].Value = !CompanionSettings.IsWorkstationHidden(WorkstationTypes[i]);
            }
            if (cbxTier != null) cbxTier.SelectedIndex = CompanionSettings.PoiMinTier;
            if (cbxZoom != null) cbxZoom.SelectedIndex = CompanionSettings.Zoom;
            for (int i = 0; i < sizeBoxes.Length; i++)
            {
                int step = System.Array.IndexOf(CompanionSettings.SizeSteps, CompanionSettings.GetSize(i));
                if (sizeBoxes[i] != null && step >= 0) sizeBoxes[i].SelectedIndex = step;
            }
        }

        private void SelectFollow()
        {
            if (cbxFollow == null) return;
            int i = followKeys.IndexOf(CompanionSettings.Follow);
            if (i < 0)
            {
                // Followed player not listed yet; the next refresh adds them
                nextRefresh = 0f;
                return;
            }
            if (cbxFollow.SelectedIndex != i) cbxFollow.SelectedIndex = i;
        }

        private void RefreshStatus()
        {
            if (lblStatus == null) return;
            lblStatus.Text = WebHost.ClientCount > 0
                ? "[80C080]Web map open in a browser[-]"
                : "Open [DECEA3]" + WebHost.Url + "[-] in a browser";
        }

        private static bool SameList(List<string> _a, List<string> _b)
        {
            if (_a.Count != _b.Count) return false;
            for (int i = 0; i < _a.Count; i++)
            {
                if (_a[i] != _b[i]) return false;
            }
            return true;
        }
    }
}
