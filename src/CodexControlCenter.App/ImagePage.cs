using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private Func<Task>? verifyImageDraft;
    public bool imageOperationRunning;
    public CancellationTokenSource? imageCancellation;
    public CancellationTokenSource? imgCts { get => imageCancellation; set => imageCancellation = value; }
    private StackPanel? imageResultPanel;
    private sealed class ImageResultEntry
    {
        public string Status { get; set; }="";
        public string? OutputPath { get; set; }
        public string? Diagnostic { get; set; }
    }
    private readonly List<ImageResultEntry> imageResultItems = [];
    private ComboBox? imageFormatChoice;
    private NumberBox? imageTimeoutBox;
    private TextBlock? imageStatusLabel;
    private Button? imageCancelButton;
    private Button? imageGenerateButton;
    private Action<bool>? imageSetConfigEnabled;
    private string imageOperationStatusText = "当前状态：空闲";
    private string imagePromptDraft="";

    private void RenderImages()
    {
        var s = snapshot!;
        var activeProvider = images!.ActiveProvider;
        var choice = Choice("默认生图来源", ["i2m / 114514", "官方 OpenAI Images API"], activeProvider == "official" ? "官方 OpenAI Images API" : "i2m / 114514", false);
        Add(choice);
        var form = Section();
        var formCard=Card(form);
        TextBox? modelEditor = null;

        void Select()
        {
            string provider = choice.SelectedIndex == 1 ? "official" : "i2m";
            ImageProfile p = images.Load(provider);
            ClearPanelChildren(form);
            var url = Input("Base URL", p.BaseUrl);
            var model = Input("模型 ID", p.Model);
            modelEditor = model;
            var size = Choice("图片尺寸", ["1024x1024", "1536x1024", "1024x1536", "auto"], p.Size);
            var quality = Choice("质量", provider == "official" ? ["auto", "low", "medium", "high"] : ["low", "medium", "high", "max", "auto"], p.Quality);
            var format = Choice("图片格式", ["png", "jpeg", "webp"], p.Format);
            imageFormatChoice = format;
            var timeout = new NumberBox
            {
                Header = "超时（秒）",
                Minimum = 30,
                Maximum = 1800,
                Value = p.Timeout,
                FontSize = bodySize,
                MinHeight = 52
            };
            imageTimeoutBox = timeout;
            double observedTimeout = timeout.Value;
            timeout.ValueChanged += (_, _) =>
            {
                if (timeout.Value == observedTimeout)
                {
                    return;
                }

                observedTimeout = timeout.Value;
                MarkDirty();
            };
            var key = new PasswordBox
            {
                Header = "替换独立 API Key（留空保持现有凭据）",
                FontSize = bodySize,
                MinHeight = 52
            };
            var keyOwner=pageInstance;key.RegisterPropertyChangedCallback(PasswordBox.PasswordProperty,(_,_)=>{if(ReferenceEquals(keyOwner,pageInstance))MarkDirty();});
            var credential = Note(string.IsNullOrEmpty(p.CredentialFile) || !File.Exists(p.CredentialFile) ? "凭据尚未配置" : "已配置当前用户加密凭据");
            foreach (var c in new UIElement[] { url, model, size, quality, format, timeout, key, credential })
            {
                form.Children.Add(c);
            }

            form.Children.Add(Note(provider == "official"
                ? "官方 Images API 使用独立 API 凭据，不使用 ChatGPT Plus 登录令牌。模型 ID 请填写账户可用的官方图像模型。"
                : "复用你已有的 i2m 脚本与加密凭据。保留现有参考图与编辑调用方式。"));
            ImageProfile Draft() => new()
            {
                Provider = provider,
                BaseUrl = url.Text.Trim(),
                Model = model.Text.Trim(),
                Size = size.SelectedItem?.ToString() ?? "1024x1024",
                Quality = quality.SelectedItem?.ToString() ?? "high",
                Format = format.SelectedItem?.ToString() ?? "png",
                Timeout = (int)timeout.Value,
                CredentialFile = key.Password.Length > 0
                    ? Path.Combine(s.CodexHome, "secrets", "control-center-image-" + provider + ".dpapi")
                    : p.CredentialFile
            };
            planFactory = () =>
            {
                var plan = images.Plan(s, Draft());
                AddCredential(plan, Draft().CredentialFile, key.Password);
                return plan;
            };
            form.Children.Add(Note("应用会同步默认来源、调用入口和 AGENTS.md 的默认生图段落。用户当次指定的服务仍优先；失败不自动更换接口或重试付费请求。"));
            ApplyButton.IsEnabled = true;
        }

        int previous = choice.SelectedIndex;
        bool reverting = false;
        choice.SelectionChanged += (_, _) =>
        {
            if (reverting || choice.SelectedIndex == previous)
            {
                return;
            }

            if (dirty)
            {
                reverting = true;
                choice.SelectedIndex = previous;
                reverting = false;
                ShowNotice("生图草稿尚未保存", "请先应用或放弃草稿后切换来源。", InfoBarSeverity.Warning);
                return;
            }

            try{Select();previous = choice.SelectedIndex;MarkDirty();}
            catch(Exception error){reverting=true;choice.SelectedIndex=previous;reverting=false;ShowError("读取生图来源失败",error);}
        };
        Select();

        var tests = Section("手动测试");
        var prompt = new TextBox
        {
            Header = "测试提示词",
            Text = imagePromptDraft,
            FontSize = bodySize,
            MinHeight = 52,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "描述要生成的图像"
        };
        prompt.TextChanging+=(_,_)=>imagePromptDraft=prompt.Text;
        tests.Children.Add(prompt);
        tests.Children.Add(Note("每次点击只发起一次请求，可能产生费用；失败不会自动重试，也不会自动切换接口。"));
        void SetConfigEnabled(bool enabled)
        {
            foreach (var child in form.Children.OfType<Control>())
            {
                child.IsEnabled = enabled;
            }
            choice.IsEnabled = enabled;
            prompt.IsEnabled = enabled;
        }
        imageSetConfigEnabled = SetConfigEnabled;
        var statusLabel = Text(imageOperationStatusText, 16);
        imageStatusLabel = statusLabel;
        tests.Children.Add(statusLabel);
        var cancelButton = new Button
        {
            Content = "取消本次生成",
            IsEnabled = false,
            MinHeight = bodySize > 18 ? 56 : 50,
            FontSize = bodySize
        };
        tests.Children.Add(cancelButton);
        imageCancelButton = cancelButton;
        var generateButton = AsyncAction("测试生成一次（可能计费）", async () =>
        {
            if (imageOperationRunning)
            {
                ShowNotice("已有生成请求", "请先取消当前请求，再发起下一次。", InfoBarSeverity.Warning);
                return;
            }

            if (dirty)
            {
                ShowNotice("请先应用配置", "测试使用已保存的默认来源。", InfoBarSeverity.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(prompt.Text))
            {
                ShowNotice("请输入提示词", "测试不会自动生成默认内容。", InfoBarSeverity.Warning);
                return;
            }

            var providerName = images.ActiveProvider;
            var selectedFormat = ImageGenerationRunner.ParseFormat(imageFormatChoice?.SelectedItem?.ToString() ?? "png");
            var outputDirectory = Path.Combine(stateHome, "generated", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
            var dialog = new ContentDialog
            {
                Title = "发起一次生图请求",
                Content = $"来源：{providerName}\n提示词：{prompt.Text}\n\n本次只发起一个请求，可能产生费用；失败不自动重试。",
                PrimaryButtonText = "生成一次",
                CloseButtonText = "取消",
                XamlRoot = Root.XamlRoot
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            var resultItem = new ImageResultEntry { Status="准备中……" };
            imageResultItems.Add(resultItem);
            RefreshImageResults();

            imageOperationRunning = true;
            RefreshDraftChrome();
            imageCancellation = new CancellationTokenSource();
            var operationToken = imageCancellation.Token;
            imageSetConfigEnabled?.Invoke(false);
            imageCancelButton?.IsEnabled = true;
            ApplyButton.IsEnabled = false;
            DiscardButton.IsEnabled = false;
            SetImageStatus("当前状态：正在生成（单次请求，未自动重试）");
            SetFooter("生图请求进行中 · 不自动重试");
            try
            {
                var runner = new ImageGenerationRunner(ResolvePythonExecutable(), images.DispatcherPath);
                var result = await runner.RunAsync(
                    new ImageGenerationRequest(s.CodexHome, prompt.Text, outputDirectory, selectedFormat, TimeSpan.FromSeconds(imageTimeoutBox?.Value ?? 600)),
                    operationToken);

                if (result.Status == ImageGenerationStatus.Succeeded && result.OutputPath is not null)
                {
                    resultItem.Status = "生成完成 · 已校验图片签名和文件大小";
                    resultItem.OutputPath=result.OutputPath;RefreshImageResults();
                    SetImageStatus("当前状态：生成完成");
                    SetFooter("图片已保存 · 未自动重试或切换接口");
                    return;
                }

                var message = result.Status switch
                {
                    ImageGenerationStatus.Cancelled => "已取消本地请求，未自动重试。请先检查结果，再决定是否重新生成。",
                    ImageGenerationStatus.TimedOut => "本地等待已超时，未自动重试。请先检查服务和结果，再决定是否重新生成。",
                    _ => "本次生成未完成，请展开诊断查看服务返回的信息；确认原因后再手动重试。"
                };
                resultItem.Status = message;
                var diagnostic = UserFeedback.Redact(string.Join("\n", new[] { result.Message,result.StandardOutput, result.StandardError }.Where(x => !string.IsNullOrWhiteSpace(x))));
                if (diagnostic.Length > 0)
                {
                    resultItem.Diagnostic=diagnostic.Length>65536?diagnostic[..65536]+"\n[诊断过长，已截断]":diagnostic;
                }
                RefreshImageResults();
                SetImageStatus(result.Status == ImageGenerationStatus.Cancelled
                    ? "当前状态：已取消"
                    : result.Status == ImageGenerationStatus.TimedOut
                        ? "当前状态：已超时"
                        : "当前状态：生成失败");
                SetFooter("本次生成已结束 · 未自动重试，请先检查结果");
                ShowNotice("生图未完成", message, result.Status == ImageGenerationStatus.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                resultItem.Status = "生成未完成，请按下方提示检查设置。";resultItem.Diagnostic=Safe(ex.Message);RefreshImageResults();
                SetImageStatus("当前状态：生成失败");
                SetFooter("生图失败 · 未自动重试，可手动重试");
                ShowError("生图失败", ex);
            }
            finally
            {
                imageOperationRunning = false;
                imageCancellation?.Dispose();
                imageCancellation = null;
                imageSetConfigEnabled?.Invoke(true);
                imageCancelButton?.IsEnabled = false;
                imageGenerateButton?.IsEnabled = true;
                RefreshDraftChrome();
            }
        });
        imageGenerateButton = generateButton;
        tests.Children.Add(generateButton);
        Add(TileGrid(520,formCard,Card(tests)));

        var resultSection = Section("生成结果");
        imageResultPanel = resultSection;
        RefreshImageResults();

        Add(Card(resultSection));
        imageSetConfigEnabled?.Invoke(!imageOperationRunning);
        imageCancelButton!.IsEnabled = imageOperationRunning;
        imageGenerateButton!.IsEnabled = !imageOperationRunning;

        cancelButton.Click += (_, _) =>
        {
            if (!imageOperationRunning || imageCancellation is null)
            {
                return;
            }
            SetImageStatus("当前状态：正在取消并停止进程……");
            imageCancelButton!.IsEnabled = false;
            imageCancellation.Cancel();
        };

        verifyImageDraft = async () =>
        {
            if (imageOperationRunning)
            {
                throw new InvalidOperationException("生图仍在运行，草稿检查不能修改本页配置");
            }

            if (modelEditor is null)
            {
                return;
            }

            modelEditor.Text += "-dry-check";
            await Task.Delay(100);
            choice.SelectedIndex = previous == 0 ? 1 : 0;
            await Task.Delay(100);
            if (choice.SelectedIndex != previous)
            {
                throw new InvalidOperationException("生图草稿切换未保护");
            }

            dirty = false;
        };
    }


    private void RefreshImageResults()
    {
        if(imageResultPanel is null)return;ClearPanelChildren(imageResultPanel);imageResultPanel.Children.Add(Text("生成结果",22));
        foreach(var item in imageResultItems.AsEnumerable().Reverse().Take(12)){
            var panel=new StackPanel{Spacing=10};panel.Children.Add(Text(item.Status,16));
            if(item.OutputPath is {} path){panel.Children.Add(new Image{Source=new BitmapImage(new Uri(Path.GetFullPath(path),UriKind.Absolute)),MaxHeight=560,Stretch=Microsoft.UI.Xaml.Media.Stretch.Uniform});panel.Children.Add(Note(path));panel.Children.Add(AsyncAction("打开图片所在文件夹",async()=>{string? folder=Path.GetDirectoryName(path);if(folder is null||!Directory.Exists(folder))throw new DirectoryNotFoundException("图片目录已不存在。");await Windows.System.Launcher.LaunchFolderPathAsync(folder);}));}
            if(!string.IsNullOrEmpty(item.Diagnostic))panel.Children.Add(new Expander{Header="查看诊断（敏感字段已隐藏）",Content=new TextBox{Text=item.Diagnostic,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=240,FontSize=15},HorizontalAlignment=HorizontalAlignment.Stretch});
            imageResultPanel.Children.Add(panel);
        }
    }

    private void SetImageStatus(string text)
    {
        imageOperationStatusText = text;
        if (imageStatusLabel is not null)
        {
            imageStatusLabel.Text = text;
        }
    }
}













