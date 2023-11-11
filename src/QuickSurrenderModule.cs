using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Extended;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Gw2Sharp.Models;
using Gw2Sharp.WebApi;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Nekres.QuickSurrender.Properties;
using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading.Tasks;

namespace Nekres.Quick_Surrender_Module {

    [Export(typeof(Module))]
    public class QuickSurrenderModule : Module
    {
        private static readonly Logger Logger = Logger.GetLogger(typeof(QuickSurrenderModule));

        internal static QuickSurrenderModule Instance;

        #region Service Managers
        internal SettingsManager SettingsManager => this.ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => this.ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => this.ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => this.ModuleParameters.Gw2ApiManager;
        #endregion

        [ImportingConstructor]
        public QuickSurrenderModule([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) { Instance = this; }

        private Texture2D _surrenderFlagHover;
        private Texture2D _surrenderFlag;
        private Texture2D _surrenderFlagPressed;

        private Image _surrenderButton;

        private SettingEntry<bool> _surrenderButtonEnabled;
        private SettingEntry<KeyBinding> _surrenderBinding;
        private SettingEntry<Ping> _surrenderPing;
        private SettingEntry<KeyBinding> _chatMessageKeySetting;

        private const string SURRENDER_TEXT = "/gg";
        private const int COOLDOWN_MS = 5000; // Cooldown of the invulnerable buff (Resurrection) after which defeat can be conceded again.

        private DateTime _lastSurrenderTime;

        private enum Ping {
            GG,
            FF,
            QQ,
            Surrender,
            Concede,
            Forfeit,
            Resign,
            // French
            Capituler,
            Abandonner,
            Renoncer,
            Concéder,
            // German
            Kapitulieren,
            Resignieren,
            Ergeben,
            Aufgeben,
            // Spanish
            Rendirse,
            Renunciar,
            Capitular,
        }

        protected override void DefineSettings(SettingCollection settings) {
            _surrenderButtonEnabled = settings.DefineSetting("SurrenderButtonEnabled", true, 
                                                             () => Resources.Show_Surrender_Skill,
                                                             () => Resources.Displays_a_skill_to_assist_in_conceding_defeat_);
            _surrenderPing = settings.DefineSetting("SurrenderButtonPing", Ping.GG, 
                                                    () => Resources.Chat_Display,
                                                    () => Resources.Determines_how_the_surrender_skill_is_displayed_in_chat_using__Ctrl__or__Shift_____Left_Mouse__);

            var keyBindingCol = settings.AddSubCollection("Hotkey", true, () => Resources.Hotkeys);
            _surrenderBinding = keyBindingCol.DefineSetting("SurrenderButtonKey", new KeyBinding(Keys.OemPeriod),
                                                            () => Resources.Surrender, 
                                                            () => Resources.Concede_defeat_by_finishing_yourself_);

            var controlOptions = settings.AddSubCollection("control_options", true, () => $"{Resources.Control_Options} ({Resources.User_Interface})");
            _chatMessageKeySetting = controlOptions.DefineSetting("ChatMessageKey", new KeyBinding(Keys.Enter),
                                                                  () => Resources.Chat_Message,
                                                                  () => Resources.Give_focus_to_the_chat_edit_box_);
        }

        protected override void Initialize() {
            LoadTextures();
        }

        private void LoadTextures() {
            _surrenderFlag = ContentsManager.GetTexture("surrender_flag.png");
            _surrenderFlagHover = ContentsManager.GetTexture("surrender_flag_hover.png");
            _surrenderFlagPressed = ContentsManager.GetTexture("surrender_flag_pressed.png");
        }

        protected override void OnModuleLoaded(EventArgs e) {
            _surrenderBinding.Value.Enabled        =  true;
            _surrenderBinding.Value.Activated      += OnSurrenderBindingActivated;
            _surrenderButtonEnabled.SettingChanged += OnSurrenderButtonEnabledSettingChanged;

            GameService.Gw2Mumble.UI.IsMapOpenChanged               += OnIsMapOpenChanged;
            GameService.GameIntegration.Gw2Instance.IsInGameChanged += OnIsInGameChanged;
            GameService.Graphics.SpriteScreen.Resized               += OnSpriteScreenResized;
            GameService.Overlay.UserLocaleChanged                   += OnUserLocaleChanged;
            BuildSurrenderButton();

            // Base handler must be called
            base.OnModuleLoaded(e);
        }

        private void OnUserLocaleChanged(object sender, ValueEventArgs<CultureInfo> e) {
            BuildSurrenderButton();
        }

        /// <inheritdoc />
        protected override void Unload() {
            _surrenderBinding.Value.Activated      -= OnSurrenderBindingActivated;
            _surrenderButtonEnabled.SettingChanged -= OnSurrenderButtonEnabledSettingChanged;

            GameService.Gw2Mumble.UI.IsMapOpenChanged               -= OnIsMapOpenChanged;
            GameService.GameIntegration.Gw2Instance.IsInGameChanged -= OnIsInGameChanged;
            GameService.Graphics.SpriteScreen.Resized               -= OnSpriteScreenResized;
            GameService.Overlay.UserLocaleChanged                   -= OnUserLocaleChanged;

            _surrenderButton?.Dispose();
            _surrenderFlag?.Dispose();
            _surrenderFlagHover?.Dispose();
            _surrenderFlagPressed?.Dispose();

            // All static members must be manually unset
            Instance = null;
        }


        private async Task DoSurrender() {
            if (GameService.Gw2Mumble.CurrentMap.Type != MapType.Instance) {
                return;
            }

            if (DateTime.UtcNow.Subtract(_lastSurrenderTime).TotalMilliseconds < COOLDOWN_MS) {
                ScreenNotification.ShowNotification(Resources.Skill_recharging_, ScreenNotification.NotificationType.Error);
                return;
            }
            _lastSurrenderTime = DateTime.UtcNow;

            _surrenderBinding.Value.Enabled = false;
            await ChatUtil.Send(SURRENDER_TEXT, _chatMessageKeySetting.Value, Logger);
            _surrenderBinding.Value.Enabled = true;
        }

        private async void OnSurrenderBindingActivated(object            o, EventArgs                   e) => await DoSurrender();
        private       void OnIsMapOpenChanged(object                     o, ValueEventArgs<bool>        e) => ToggleSurrenderButton(!e.Value,   0.45f);
        private       void OnIsInGameChanged(object                      o, ValueEventArgs<bool>        e) => ToggleSurrenderButton(e.Value,    0.1f);
        private       void OnSurrenderButtonEnabledSettingChanged(object o, ValueChangedEventArgs<bool> e) => ToggleSurrenderButton(e.NewValue, 0.1f);

        private void ToggleSurrenderButton(bool enabled, float tDuration) {
            if (enabled) {
                BuildSurrenderButton();
            } else if (_surrenderButton != null) {
                GameService.Animation.Tweener.Tween(_surrenderButton, new { Opacity = 0.0f }, tDuration).OnComplete(() => _surrenderButton?.Dispose());
            }
        }

        private void BuildSurrenderButton() {
            _surrenderButton?.Dispose();

            if (!_surrenderButtonEnabled.Value || GameService.Gw2Mumble.CurrentMap.Type != MapType.Instance) {
                return;
            }

            var tooltipSize = new Point(300, GameService.Overlay.UserLocale.Value == Locale.French ? 120 : 100); // dirty clipping fix.
            var surrenderButtonTooltip = new Tooltip {
                Size = tooltipSize
            };

            // Create the ability tooltip
            var cooldownLabel = new FormattedLabelBuilder().SetWidth(tooltipSize.X)
                                                           .SetHeight(tooltipSize.Y)
                                                           .SetHorizontalAlignment(HorizontalAlignment.Right)
                                                           .SetVerticalAlignment(VerticalAlignment.Top)
                                                           .CreatePart($"{Math.Round(COOLDOWN_MS / 1000f)}", o => {
                                                                o.SetFontSize(ContentService.FontSize.Size16);
                                                                o.SetSuffixImageSize(new Point(18, 18));
                                                                o.SetSuffixImage(GameService.Content.DatAssetCache.GetTextureFromAssetId(156651));
                                                            }).Build();
            cooldownLabel.Parent = surrenderButtonTooltip;

            var label = new FormattedLabelBuilder().SetWidth(tooltipSize.X)
                                                   .SetHeight(tooltipSize.Y)
                                                   .SetVerticalAlignment(VerticalAlignment.Top)
                                                   .CreatePart(Resources.Surrender + '\n', o => {
                                                        o.SetTextColor(new Color(255, 204, 119));
                                                        o.MakeBold();
                                                        o.SetFontSize(ContentService.FontSize.Size20);
                                                    })
                                                   .CreatePart(Resources.Chat_Command + ". ", o => {
                                                        o.SetTextColor(new Color(240, 224, 129));
                                                        o.SetFontSize(ContentService.FontSize.Size16);
                                                    })
                                                   .CreatePart(Resources.Concede_defeat_by_finishing_yourself_ + '\n', o => {
                                                        o.SetFontSize(ContentService.FontSize.Size16);
                                                    })
                                                   .CreatePart(Resources.You_are_defeated_, o => {
                                                        o.SetTextColor(new Color(175, 175, 175));
                                                        o.SetFontSize(ContentService.FontSize.Size16);
                                                        o.SetPrefixImage(GameService.Content.DatAssetCache.GetTextureFromAssetId(102540));
                                                    }).Wrap().Build();
            label.Parent = surrenderButtonTooltip;
            
            _surrenderButton = new Image {
                Parent  = GameService.Graphics.SpriteScreen,
                Size    = new Point(45, 45),
                Texture = _surrenderFlag,
                Tooltip = surrenderButtonTooltip,
                Opacity = 0.0f
            };

            _surrenderButton.MouseEntered += (_,_) => _surrenderButton.Texture = _surrenderFlagHover;
            _surrenderButton.MouseLeft    += (_,_) => _surrenderButton.Texture = _surrenderFlag;

            _surrenderButton.LeftMouseButtonPressed += (_,_) => {
                _surrenderButton.Size = new Point(43, 43);
                _surrenderButton.Texture = _surrenderFlagPressed;
            };

            _surrenderButton.LeftMouseButtonReleased += (_,_) => {
                _surrenderButton.Size = new Point(45, 45);
                _surrenderButton.Texture = _surrenderFlag;
            };

            _surrenderButton.Click += async (_,_) => {
                // Paste as ability name (aka. ping) when modifiers are held when clicking.
                if (KeyboardUtil.IsCtrlPressed()) {
                    await ChatUtil.Send($"[/{_surrenderPing.Value}]", _chatMessageKeySetting.Value, Logger);
                } else if (KeyboardUtil.IsShiftPressed()) {
                    await ChatUtil.Insert($"[/{_surrenderPing.Value}]", _chatMessageKeySetting.Value, Logger);
                } else {
                    await DoSurrender();
                }
            };

            ValidatePosition();
            GameService.Animation.Tweener.Tween(_surrenderButton, new {Opacity = 1.0f}, 0.35f);
        }

        private void ValidatePosition() {
            if (_surrenderButton == null) {
                return;
            }

            _surrenderButton.Location = new Point(GameService.Graphics.SpriteScreen.Width / 2 - _surrenderButton.Width  / 2 + 431,
                                                  GameService.Graphics.SpriteScreen.Height    - _surrenderButton.Height * 2 + 7);
        }

        private void OnSpriteScreenResized(object sender, ResizedEventArgs e) {
            ValidatePosition();
        }
    }
}
