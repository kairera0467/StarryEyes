﻿using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Livet;
using Livet.EventListeners;
using Livet.Messaging.IO;
using StarryEyes.Annotations;
using StarryEyes.Anomaly.TwitterApi.DataModels;
using StarryEyes.Helpers;
using StarryEyes.Models;
using StarryEyes.Models.Backstages.NotificationEvents.PostEvents;
using StarryEyes.Models.Inputting;
using StarryEyes.Models.Requests;
using StarryEyes.Models.Subsystems;
using StarryEyes.Nightmare.Windows;
using StarryEyes.Settings;
using StarryEyes.ViewModels.Timelines.Statuses;
using StarryEyes.Views.Messaging;
using Clipboard = System.Windows.Clipboard;

namespace StarryEyes.ViewModels.WindowParts.Inputting
{
    public class InputCoreViewModel : ViewModel
    {
        private readonly InputViewModel _parent;
        private readonly DispatcherCollection<BindHashtagViewModel> _bindableHashtagCandidates;

        private readonly ReadOnlyDispatcherCollectionRx<BindHashtagViewModel> _bindingHashtags;
        private readonly ReadOnlyDispatcherCollectionRx<InputDataViewModel> _draftedInputs;
        private readonly InputAreaSuggestItemProvider _provider;

        private GeoCoordinateWatcher _geoWatcher;
        private UserViewModel _recipientViewModel;
        private InReplyToStatusViewModel _inReplyToViewModelCache;
        private bool _isLocationEnabled;

        private readonly string _tempDir;

        public InputCoreViewModel(InputViewModel parent)
        {
            this._parent = parent;
            this._provider = new InputAreaSuggestItemProvider();

            CompositeDisposable.Add(
                this._bindingHashtags = ViewModelHelperRx.CreateReadOnlyDispatcherCollectionRx(
                    InputModel.InputCore.BindingHashtags,
                    tag => new BindHashtagViewModel(tag, () => UnbindHashtag(tag)),
                    DispatcherHelper.UIDispatcher));
            CompositeDisposable.Add(_bindingHashtags
                                        .ListenCollectionChanged()
                                        .Subscribe(_ =>
                                        {
                                            InputData.BoundTags = _bindingHashtags.Select(h => h.Hashtag).ToArray();
                                            RaisePropertyChanged(() => IsBindingHashtagExisted);
                                        }));
            _bindableHashtagCandidates =
                new DispatcherCollection<BindHashtagViewModel>(DispatcherHelper.UIDispatcher);
            CompositeDisposable.Add(_bindableHashtagCandidates
                                        .ListenCollectionChanged()
                                        .Subscribe(_ => RaisePropertyChanged(() => IsBindableHashtagExisted)));

            CompositeDisposable.Add(_draftedInputs =
                                    ViewModelHelperRx.CreateReadOnlyDispatcherCollectionRx(
                    InputModel.InputCore.Drafts,
                                        _ =>
                                        new InputDataViewModel(this, _, vm => InputModel.InputCore.Drafts.Remove(vm)),
                                        DispatcherHelper.UIDispatcher));

            CompositeDisposable.Add(_draftedInputs
                                        .ListenCollectionChanged()
                                        .Subscribe(_ =>
                                        {
                                            RaisePropertyChanged(() => DraftCount);
                                            RaisePropertyChanged(() => IsDraftsExisted);
                                        }));

            // listen setting changed
            CompositeDisposable.Add(
                Setting.SuppressTagBindingInReply.ListenValueChanged(
                    _ => RaisePropertyChanged(() => IsBindHashtagEnabled)));

            // listen text control
            CompositeDisposable.Add(new EventListener<Action<CursorPosition>>(
                h => InputModel.SetCursorRequest += h,
                h => InputModel.SetCursorRequest -= h,
                SetCursor));
            var plistener = new PropertyChangedEventListener(InputModel.InputCore);
            plistener.Add(() => InputModel.InputCore.CurrentInputData, (_, e) => InputDataChanged());
            CompositeDisposable.Add(plistener);

            // create temporary directory and reserve deletion before exit app.
            do
            {
                _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            } while (Directory.Exists(_tempDir));
            Directory.CreateDirectory(_tempDir);
            App.ApplicationExit += () =>
            {
                try
                {
                    Directory.Delete(this._tempDir, true);
                }
                catch
                {
                    // I think that is sign from God that I must not delete that folder if failed.
                }
            };

            // initialize clipboard watcher.
            ClipboardWatcher watcher;
            CompositeDisposable.Add(watcher = new ClipboardWatcher());
            watcher.ClipboardChanged += (o, e) => RaisePropertyChanged(() => IsClipboardContentImage);
            watcher.StartWatching();
            Setting.DisableGeoLocationService.ValueChanged += this.UpdateGeoLocationService;
            this.UpdateGeoLocationService(Setting.DisableGeoLocationService.Value);
        }

        private void UpdateGeoLocationService(bool isEnabled)
        {
            if (isEnabled == (_geoWatcher != null))
            {
                // not changed
                return;
            }
            if (isEnabled)
            {
                // enable
                _geoWatcher = new GeoCoordinateWatcher();
                _geoWatcher.StatusChanged += (_, e) =>
                {
                    if (e.Status != GeoPositionStatus.Ready)
                    {
                        IsLocationEnabled = true;
                    }
                    else
                    {
                        IsLocationEnabled = false;
                        AttachedLocation = null;
                    }
                };
                _geoWatcher.Start();
                CompositeDisposable.Add(_geoWatcher);
            }
            else
            {
                // disable
                var watcher = _geoWatcher;
                _geoWatcher = null;
                CompositeDisposable.Remove(watcher);
                watcher.Stop();
                watcher.Dispose();
                IsLocationEnabled = false;
                AttachedLocation = null;
            }
        }

        [NotNull]
        public InputData InputData
        {
            get { return InputModel.InputCore.CurrentInputData; }
            set
            {
                InputModel.InputCore.CurrentInputData = value;
                InputDataChanged();
            }
        }

        private void InputDataChanged()
        {
            this.RaisePropertyChanged(() => InputData);
            RaisePropertyChanged(() => InputText);
            RaisePropertyChanged(() => InReplyTo);
            RaisePropertyChanged(() => IsInReplyToEnabled);
            RaisePropertyChanged(() => DirectMessageTo);
            RaisePropertyChanged(() => IsDirectMessageEnabled);
            RaisePropertyChanged(() => IsBindHashtagEnabled);
            RaisePropertyChanged(() => AttachedImage);
            RaisePropertyChanged(() => IsImageAttached);
            RaisePropertyChanged(() => AttachedLocation);
            RaisePropertyChanged(() => IsLocationAttached);
            RaisePropertyChanged(() => IsAmending);
            RaisePropertyChanged(() => CanAmend);
            UpdateHashtagCandidates();
            UpdateTextCount();
        }

        public InputAreaSuggestItemProvider Provider
        {
            get { return this._provider; }
        }

        #region Text control

        [NotNull]
        public string InputText
        {
            get { return InputData.Text; }
            set
            {
                InputData.Text = value;
                RaisePropertyChanged(() => InputText);
                UpdateHashtagCandidates();
                UpdateTextCount();
                if (IsUrlAutoEsacpeEnabled)
                {
                    EscapeUrl();
                }
            }
        }

        private void UpdateTextCount()
        {
            RaisePropertyChanged(() => TextCount);
            RaisePropertyChanged(() => RemainTextCount);
            RaisePropertyChanged(() => CanSend);
            RaisePropertyChanged(() => CanSaveToDraft);
        }

        public int TextCount
        {
            get
            {
                var currentTextLength = StatusTextUtil.CountText(InputText);
                if (IsImageAttached)
                {
                    currentTextLength += TwitterConfigurationService.HttpsUrlLength;
                }
                var tags = TwitterRegexPatterns.ValidHashtag.Matches(InputText)
                                           .OfType<Match>()
                                           .Select(_ => _.Groups[1].Value)
                                           .ToArray();
                if (InputModel.InputCore.BindingHashtags.Count > 0)
                {
                    currentTextLength += InputModel.InputCore.BindingHashtags
                                                   .Except(tags)
                                                   .Select(_ => _.Length + 1)
                                                   .Sum();
                }
                return currentTextLength;
            }
        }

        public int RemainTextCount
        {
            get { return TwitterConfigurationService.TextMaxLength - TextCount; }
        }

        public bool IsUrlAutoEsacpeEnabled
        {
            get { return Setting.IsUrlAutoEscapeEnabled.Value; }
            set
            {
                Setting.IsUrlAutoEscapeEnabled.Value = value;
                RaisePropertyChanged(() => IsUrlAutoEsacpeEnabled);
                if (value)
                {
                    EscapeUrl();
                }
            }
        }

        private void EscapeUrl()
        {
            var escaped = StatusTextUtil.AutoEscape(InputText);
            if (escaped == this.InputText) return;
            this.InputData.Text = escaped;
            this.RaisePropertyChanged(() => this.InputText);
            this.UpdateHashtagCandidates();
            this.UpdateTextCount();

            var diff = escaped.Length - this.InputText.Length;
            this.SelectionStart += diff;
        }

        public bool CheckClearInput(string clearTo = "")
        {
            if (CanSaveToDraft && InputData.IsChanged)
            {
                var action = Setting.TweetBoxClosingAction.Value;
                if (action == TweetBoxClosingAction.Confirm)
                {
                    var msg = _parent.Messenger.GetResponse(
                        new TaskDialogMessage(
                            new TaskDialogOptions
                            {
                                Title = "下書きへの保存",
                                MainIcon = VistaTaskDialogIcon.Information,
                                MainInstruction = "現在の内容を下書きに保存しますか？",
                                CustomButtons = new[] { "保存(&Y)", "破棄(&N)", "キャンセル" },
                                VerificationText = "次回から表示しない",
                                AllowDialogCancellation = true,
                            }));
                    switch (msg.Response.CustomButtonResult)
                    {
                        case 0:
                            action = TweetBoxClosingAction.SaveToDraft;
                            break;
                        case 1:
                            action = TweetBoxClosingAction.Discard;
                            break;
                        default:
                            return false;
                    }
                    if (msg.Response.VerificationChecked.GetValueOrDefault())
                    {
                        Setting.TweetBoxClosingAction.Value = action;
                    }
                }
                switch (action)
                {
                    case TweetBoxClosingAction.Discard:
                        break;
                    case TweetBoxClosingAction.SaveToDraft:
                        ClearInput(clearTo, true);
                        return true;
                    default:
                        throw new InvalidOperationException("Invalid return value:" + action.ToString());
                }
            }
            ClearInput(clearTo);
            return true;
        }

        public void ClearInput(string clearTo = "", bool sendDraftIfChanged = false)
        {
            InputModel.InputCore.ClearInput(clearTo, sendDraftIfChanged);
        }

        #endregion

        #region Replying/Messaging control

        public InReplyToStatusViewModel InReplyTo
        {
            get
            {
                if (InputData.InReplyTo == null)
                {
                    return null;
                }

                if (_inReplyToViewModelCache != null &&
                    _inReplyToViewModelCache.Id != InputData.InReplyTo.Id)
                {
                    _inReplyToViewModelCache.Dispose();
                    _inReplyToViewModelCache = null;
                }
                return this._inReplyToViewModelCache ??
                       (this._inReplyToViewModelCache = new InReplyToStatusViewModel(InputData.InReplyTo));
            }
            set
            {
                if (_inReplyToViewModelCache != null)
                {
                    _inReplyToViewModelCache.Dispose();
                }
                if (value == null)
                {
                    _inReplyToViewModelCache = null;
                    InputData.InReplyTo = null;
                }
                else
                {
                    _inReplyToViewModelCache = value;
                    InputData.InReplyTo = value.Status;
                }
                RaisePropertyChanged(() => InReplyTo);
                RaisePropertyChanged(() => IsInReplyToEnabled);
                RaisePropertyChanged(() => IsBindHashtagEnabled);
            }
        }

        public bool IsInReplyToEnabled
        {
            get { return InputData.InReplyTo != null; }
        }

        public void ClearInReplyTo()
        {
            InReplyTo = null;
        }

        public UserViewModel DirectMessageTo
        {
            get
            {
                if (InputData.MessageRecipient == null)
                {
                    return null;
                }

                if (_recipientViewModel == null ||
                    _recipientViewModel.User.Id != InputData.MessageRecipient.Id)
                {
                    _recipientViewModel = new UserViewModel(InputData.MessageRecipient);
                }
                return _recipientViewModel;
            }
            set
            {
                if (value == null)
                {
                    InputData.MessageRecipient = null;
                    _recipientViewModel = null;
                }
                else
                {
                    InputData.MessageRecipient = value.User;
                    _recipientViewModel = value;
                }
                RaisePropertyChanged(() => DirectMessageTo);
                RaisePropertyChanged(() => IsDirectMessageEnabled);
                RaisePropertyChanged(() => IsBindHashtagEnabled);
            }
        }

        public bool IsBindHashtagEnabled
        {
            get
            {
                return InputData.MessageRecipient == null ||
                       (InputData.InReplyTo == null || !Setting.SuppressTagBindingInReply.Value);
            }
        }

        public bool IsDirectMessageEnabled
        {
            get { return InputData.MessageRecipient != null; }
        }

        public void ClearDirectMessage()
        {
            DirectMessageTo = null;
        }

        #endregion

        #region Cursoring control

        private string _selectedText = "";
        private int _selectionLength;
        private int _selectionStart;

        public int SelectionStart
        {
            get { return _selectionStart; }
            set
            {
                _selectionStart = value;
                RaisePropertyChanged(() => SelectionStart);
            }
        }

        public int SelectionLength
        {
            get { return _selectionLength; }
            set
            {
                _selectionLength = value;
                RaisePropertyChanged(() => SelectionLength);
            }
        }

        public string SelectedText
        {
            get { return _selectedText; }
            set
            {
                _selectedText = value;
                RaisePropertyChanged(() => SelectedText);
            }
        }

        private void SetCursor(CursorPosition position)
        {
            this._parent.Messenger.Raise(new TextBoxSetCaretMessage(
                position.Index < 0 ? InputText.Length : position.Index, position.SelectionLength));
        }

        #endregion

        #region Hashtag bind control

        public bool IsBindableHashtagExisted
        {
            get { return _bindableHashtagCandidates != null && _bindableHashtagCandidates.Count > 0; }
        }

        public bool IsBindingHashtagExisted
        {
            get { return _bindingHashtags != null && _bindingHashtags.Count > 0; }
        }

        public DispatcherCollection<BindHashtagViewModel> BindableHashtagCandidates
        {
            get { return this._bindableHashtagCandidates; }
        }

        public ReadOnlyDispatcherCollectionRx<BindHashtagViewModel> BindingHashtags
        {
            get { return this._bindingHashtags; }
        }

        private void UpdateHashtagCandidates()
        {
            var hashtags = TwitterRegexPatterns.ValidHashtag.Matches(InputData.Text)
                                               .OfType<Match>()
                                               .Select(_ => _.Groups[1].Value)
                                               .Where(s => !String.IsNullOrEmpty(s))
                                               .Distinct()
                                               .Except(BindingHashtags.Select(_ => _.Hashtag))
                                               .ToArray();
            BindableHashtagCandidates
                .Where(_ => !hashtags.Contains(_.Hashtag))
                .ToList()
                .ForEach(_ => BindableHashtagCandidates.Remove(_));
            hashtags
                .Where(_ => !BindableHashtagCandidates.Select(t => t.Hashtag).Contains(_))
                .Select(_ => new BindHashtagViewModel(_, () => BindHashtag(_)))
                .ForEach(BindableHashtagCandidates.Add);
        }

        public void BindHashtag(string hashtag)
        {
            InputModel.BindHashtag(hashtag);
            this.UpdateHashtagCandidates();
            this.UpdateTextCount();
        }

        public void UnbindHashtag(string hashtag)
        {
            InputModel.InputCore.BindingHashtags.Remove(hashtag);
            UpdateHashtagCandidates();
            UpdateTextCount();
        }

        #endregion

        #region Image attach control

        public bool IsImageAttached
        {
            get { return InputData.AttachedImage != null; }
        }

        public ImageDescriptionViewModel AttachedImage
        {
            get
            {
                return InputData.AttachedImage != null
                    ? new ImageDescriptionViewModel(InputData.AttachedImage)
                    : null;
            }
            set
            {
                InputData.AttachedImage = value == null ? null : value.ByteArray;
                RaisePropertyChanged(() => AttachedImage);
                RaisePropertyChanged(() => IsImageAttached);
                RaisePropertyChanged(() => CanSaveToDraft);
                UpdateTextCount();
            }
        }

        public void AttachImage()
        {
            var dir = Setting.LastImageOpenDir.Value;
            if (!Directory.Exists(dir))
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }
            var msg = new OpeningFileSelectionMessage
            {
                Filter = "画像ファイル|*.jpg;*.jpeg;*.jpe;*.png;*.gif;*.bmp;*.dib|全てのファイル|*.*",
                InitialDirectory = dir,
                MultiSelect = false,
                Title = "添付する画像ファイルを指定"
            };
            var m = _parent.Messenger.GetResponse(msg);
            if (m.Response == null || m.Response.Length <= 0 ||
                String.IsNullOrEmpty(m.Response[0]) || !File.Exists(m.Response[0]))
            {
                return;
            }

            Setting.LastImageOpenDir.Value = Path.GetDirectoryName(m.Response[0]);
            AttachImageFromPath(m.Response[0]);
        }

        public void DetachImage()
        {
            AttachedImage = null;
        }

        [UsedImplicitly]
        public void AttachClipboardImage()
        {
            BitmapSource image;
            if (!Clipboard.ContainsImage() || (image = WinFormsClipboard.GetWpfImage()) == null) return;
            var tempPath = Path.Combine(_tempDir, Path.GetRandomFileName() + ".png");
            using (var fs = new FileStream(tempPath, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(fs);
            }
            AttachImageFromPath(tempPath);
        }

        private bool AttachImageFromPath(string file)
        {
            try
            {
                AttachedImage = new ImageDescriptionViewModel(file);
                return true;
            }
            catch (Exception ex)
            {
                _parent.Messenger.Raise(new TaskDialogMessage(new TaskDialogOptions
                {
                    Title = "画像読み込みエラー",
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "画像の添付ができませんでした。",
                    Content = "画像の読み込み時にエラーが発生しました。" + Environment.NewLine +
                              "未対応の画像か、データが破損しています。",
                    ExpandedInfo = ex.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close,
                }));
                AttachedImage = null;
                return false;
            }
        }

        [UsedImplicitly]
        public void StartSnippingTool()
        {
            try
            {
                Process.Start("SnippingTool.exe");
            }
            catch (Exception ex)
            {
                _parent.Messenger.Raise(new TaskDialogMessage(new TaskDialogOptions
                 {
                     Title = "エラー",
                     MainIcon = VistaTaskDialogIcon.Error,
                     MainInstruction = "Snipping Toolの起動に失敗しました。",
                     Content = "スタートメニューからの起動を試してみてください。",
                     ExpandedInfo = ex.ToString(),
                     CommonButtons = TaskDialogCommonButtons.Close
                 }));
            }
        }

        public bool IsClipboardContentImage
        {
            get { return Clipboard.ContainsImage(); }
        }

        private DropAcceptDescription _description;

        public DropAcceptDescription DropAcceptDescription
        {
            get
            {
                if (_description == null)
                {
                    _description = new DropAcceptDescription();
                    _description.DragOver += args =>
                    {
                        args.Effects = args.Data.GetData(DataFormats.FileDrop) != null
                                           ? DragDropEffects.Link
                                           : DragDropEffects.None;
                        args.Handled = true;
                    };
                    _description.DragDrop += args =>
                    {
                        var files = args.Data.GetData(DataFormats.FileDrop) as string[];
                        if (files != null && files.Length > 0)
                        {
                            AttachedImage = new ImageDescriptionViewModel(files[0]);
                        }
                    };
                }
                return _description;
            }
        }

        #endregion

        #region Location attach control

        public bool IsLocationEnabled
        {
            get { return _isLocationEnabled; }
            set
            {
                _isLocationEnabled = value;
                RaisePropertyChanged(() => IsLocationEnabled);
            }
        }

        public LocationDescriptionViewModel AttachedLocation
        {
            get
            {
                return this.InputData.AttachedGeoLocation != null
                           ? new LocationDescriptionViewModel(this.InputData.AttachedGeoLocation)
                           : null;
            }
            set
            {
                InputData.AttachedGeoLocation = value == null ? null : value.Location;
                RaisePropertyChanged(() => AttachedLocation);
                RaisePropertyChanged(() => IsLocationAttached);
            }
        }

        public bool IsLocationAttached
        {
            get { return InputData.AttachedGeoLocation != null; }
        }

        #endregion

        #region Amending control

        public bool IsAmending
        {
            get { return InputModel.InputCore.IsAmending; }
        }

        public bool CanAmend
        {
            get { return InputModel.InputCore.LastPostedData != null && !IsAmending; }
        }

        public void AmendLastPosted()
        {
            InputModel.InputCore.AmendLastPosted();
        }

        #endregion

        #region Drafting control

        public ReadOnlyDispatcherCollectionRx<InputDataViewModel> DraftedInputs
        {
            get { return this._draftedInputs; }
        }

        public bool IsDraftsExisted
        {
            get { return _draftedInputs.Count > 0; }
        }

        public int DraftCount
        {
            get { return _draftedInputs.Count; }
        }

        public bool CanSaveToDraft
        {
            get { return InputData.IsChanged; }
        }

        #endregion

        #region Posting control

        public bool CanSend
        {
            get
            {
                if (!InputModel.AccountSelector.Accounts.Any())
                {
                    return false; // send account is not found.
                }
                if (TextCount > TwitterConfigurationService.TextMaxLength)
                    return false;
                return IsImageAttached || !String.IsNullOrEmpty(
                    InputText.Replace("\t", "")
                             .Replace("\r", "")
                             .Replace("\n", "")
                             .Replace(" ", ""));
            }
        }

        public void Send()
        {
            if (!CanSend)
            {
                // could not send.
                this.RaisePropertyChanged(() => CanSend);
                this._parent.FocusToTextBox();
                return;
            }
            if (!this.CheckInput())
            {
                return;
            }
            SendCore(InputData);
            ClearInput();
            this._parent.FocusToTextBox();
        }

        private bool CheckInput()
        {
            if (InputData.IsAmend && Setting.WarnAmending.Value)
            {
                var removal = InputData.AmendTargetTweets.First().Value.ToString();
                var dual = InputData.AmendTargetTweets.Count();
                if (dual > 2)
                {
                    removal += " (" + dual + "件のツイートが同時に削除されます)";
                }
                // amend mode
                var amend = _parent.Messenger.GetResponse(
                    new TaskDialogMessage(
                        new TaskDialogOptions
                        {
                            Title = "ツイートの訂正",
                            MainIcon = VistaTaskDialogIcon.Information,
                            MainInstruction = "直前のツイートを削除し、再投稿します。",
                            Content = "削除に失敗した場合でも投稿は行われます。",
                            ExpandedInfo = "削除されるツイート: " + removal,
                            CommonButtons = TaskDialogCommonButtons.OKCancel,
                            VerificationText = "次回から表示しない",
                        }));
                Setting.WarnAmending.Value = !amend.Response.VerificationChecked.GetValueOrDefault();
                if (amend.Response.Result == TaskDialogSimpleResult.Cancel)
                {
                    return false;
                }
            }
            if (InReplyTo != null && Setting.WarnReplyFromThirdAccount.Value)
            {
                // warn third reply

                // filters screen names which were replied
                var replies = TwitterRegexPatterns.ValidMentionOrList.Matches(InReplyTo.Status.Text)
                                                  .Cast<Match>()
                                                  .Select(_ => _.Groups[TwitterRegexPatterns.ValidMentionOrListGroupUsername].Value.Substring(1))
                                                  .Where(_ => !String.IsNullOrEmpty(_))
                                                  .Distinct()
                                                  .ToArray();

                // check third-reply mistake.
                if (!Setting.Accounts
                            .Collection
                            .Select(a => a.UnreliableScreenName)
                            .Any(replies.Contains) &&
                    InputData.Accounts
                             .Guard()
                             .Select(_ => _.UnreliableScreenName)
                             .Any(replies.Contains))
                {
                    var thirdreply = _parent.Messenger.GetResponse(
                        new TaskDialogMessage(new TaskDialogOptions
                        {
                            Title = "割込みリプライ警告",
                            MainIcon = VistaTaskDialogIcon.Warning,
                            Content = "違うアカウントから会話を継続しようとしています。" + Environment.NewLine +
                                      "投稿してもよろしいですか？",
                            VerificationText = "次回から表示しない",
                            CommonButtons = TaskDialogCommonButtons.OKCancel,
                        }));
                    Setting.WarnReplyFromThirdAccount.Value =
                        !thirdreply.Response.VerificationChecked.GetValueOrDefault();
                    if (thirdreply.Response.Result == TaskDialogSimpleResult.Cancel)
                        return false;
                }
            }
            return true;
        }

        private void SendCore(InputData data)
        {
            data.Send()
                .Subscribe(r =>
                {
                    if (r.Succeededs != null)
                    {
                        InputModel.InputCore.LastPostedData = r.Succeededs;
                        BackstageModel.RegisterEvent(new PostSucceededEvent(r.Succeededs));
                    }
                    if (r.Faileds != null)
                    {
                        var message = this.AnalyzeFailedReason(r.Exceptions) ?? "原因を特定できませんでした。";
                        var ed = r.Exceptions
                                  .Guard()
                                  .SelectMany(ex => EnumerableEx.Generate(
                                      ex, e => e != null, e => e.InnerException, e => e))
                                  .Where(e => e != null)
                                  .Select(e => e.ToString())
                                  .JoinString(Environment.NewLine);
                        if (Setting.ShowMessageOnTweetFailed.Value)
                        {
                            var resp = _parent.Messenger.GetResponse(new TaskDialogMessage(new TaskDialogOptions
                            {
                                Title = "ツイートの失敗",
                                MainIcon = VistaTaskDialogIcon.Error,
                                MainInstruction = "ツイートに失敗しました。",
                                Content = "エラー: " + message + Environment.NewLine +
                                          "もう一度投稿しますか？",
                                ExpandedInfo = ed,
                                FooterText = "再試行しない場合は、ツイートしようとした内容は下書きとして保存されます。",
                                FooterIcon = VistaTaskDialogIcon.Information,
                                VerificationText = "次回から表示しない",
                                CommonButtons = TaskDialogCommonButtons.RetryCancel
                            }));
                            Setting.ShowMessageOnTweetFailed.Value =
                                !resp.Response.VerificationChecked.GetValueOrDefault();
                            if (resp.Response.Result == TaskDialogSimpleResult.Retry)
                            {
                                this.SendCore(r.Faileds);
                                return;
                            }
                        }
                        else
                        {
                            BackstageModel.RegisterEvent(new PostFailedEvent(r.Faileds, message));
                        }
                        // Send to draft
                        InputModel.InputCore.Drafts.Add(r.Faileds);
                    }
                });
        }

        private string AnalyzeFailedReason(IEnumerable<Exception> exceptions)
        {
            if (exceptions == null) return null;
            string fmsg = null;
            foreach (var exception in exceptions)
            {
                var msg = exception.Message;
                if (fmsg == null)
                {
                    // stash first message
                    fmsg = msg;
                    var cex = exception.InnerException;
                    while (cex != null)
                    {
                        fmsg += " - " + cex.Message;
                        cex = cex.InnerException;
                    }
                }
                if (msg.Contains("duplicate"))
                {
                    return "直近のツイートと重複しています。";
                }
                if (msg.Contains("User is over daily update limit."))
                {
                    return "POST規制されています。";
                }
                // TODO: Implement more cases.
            }
            return fmsg;
        }

        #endregion
    }

    public class InReplyToStatusViewModel : ViewModel
    {
        private readonly TwitterStatus _status;

        public InReplyToStatusViewModel(TwitterStatus status)
        {
            this._status = status;
        }

        public long Id
        {
            get { return this.Status.Id; }
        }

        public string ScreenName
        {
            get { return this.Status.User.ScreenName; }
        }

        public string Text
        {
            get { return this.Status.Text; }
        }

        public TwitterStatus Status
        {
            get { return this._status; }
        }
    }

    public class ImageDescriptionViewModel : ViewModel
    {
        private BitmapImage _bitmap;
        private byte[] _byteArray;

        public ImageDescriptionViewModel(string filePath)
            : this(File.ReadAllBytes(filePath))
        {
        }

        public ImageDescriptionViewModel(byte[] image)
        {
            ByteArray = image;
        }

        public ImageDescriptionViewModel(BitmapImage image, ImageType sourceType)
        {
            this._bitmap = image;
            _byteArray = image.SaveToBytes(sourceType);
        }

        public byte[] ByteArray
        {
            get { return this._byteArray; }
            set
            {
                this._byteArray = value;
                _bitmap = ImageUtil.CreateImage(value);
                RaisePropertyChanged();
                RaisePropertyChanged(() => Image);
            }
        }

        public BitmapImage Image
        {
            get { return _bitmap; }
        }
    }

    public class LocationDescriptionViewModel : ViewModel
    {
        public LocationDescriptionViewModel(GeoCoordinate geoCoordinate)
        {
            Location = new GeoLocationInfo
            {
                Latitude = geoCoordinate.Latitude,
                Longitude = geoCoordinate.Longitude,
            };
        }

        public LocationDescriptionViewModel(GeoLocationInfo locInfo)
        {
            Location = locInfo;
        }

        public GeoLocationInfo Location { get; set; }
    }
}
