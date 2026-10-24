using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine.Scripting;

namespace WebCompanion
{
    // The "Web Companion" panel on the right side of the map screen (M). Window in Config/XUi_InGame/windows.xml.
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
                for (int z = CompanionSettings.MinZoom; z <= CompanionSettings.MaxZoom; z++) cbxZoom.Elements.Add(CompanionSettings.ZoomName(z));
                cbxZoom.OnValueChanged += (_sender, _old, _new) => CompanionSettings.SetZoom(CompanionSettings.MinZoom + cbxZoom.SelectedIndex);
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

            XUiController status = GetChildById("status");
            lblStatus = status != null ? status.ViewComponent as XUiV_Label : null;
        }

        public override void OnOpen()
        {
            base.OnOpen();
            seenVersion = -1;
            nextRefresh = 0f;
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
            if (cbxTier != null) cbxTier.SelectedIndex = CompanionSettings.PoiMinTier;
            if (cbxZoom != null) cbxZoom.SelectedIndex = CompanionSettings.Zoom - CompanionSettings.MinZoom;
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
                ? "[80C080]Companion page open[-]"
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
