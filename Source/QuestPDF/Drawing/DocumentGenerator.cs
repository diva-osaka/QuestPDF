using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuestPDF.Drawing.Exceptions;
using QuestPDF.Drawing.Proxy;
using QuestPDF.Elements;
using QuestPDF.Elements.Text;
using QuestPDF.Elements.Text.Items;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Diva.FontSubsetting;

namespace QuestPDF.Drawing
{
    static class DocumentGenerator
    {
        static DocumentGenerator()
        {
            NativeDependencyCompatibilityChecker.Test();
        }
        
        internal static void GeneratePdf(Stream stream, IDocument document)
        {
            CheckIfStreamIsCompatible(stream);
            
            var metadata = document.GetMetadata();
            var canvas = new PdfCanvas(stream, metadata);
            RenderDocument(canvas, document);
        }
        
        internal static void GenerateXps(Stream stream, IDocument document)
        {
            CheckIfStreamIsCompatible(stream);
            
            var metadata = document.GetMetadata();
            var canvas = new XpsCanvas(stream, metadata);
            RenderDocument(canvas, document);
        }

        private static void CheckIfStreamIsCompatible(Stream stream)
        {
            if (!stream.CanWrite)
                throw new ArgumentException("The library requires a Stream object with the 'write' capability available (the CanWrite flag). Please consider using the MemoryStream class.");
            
            if (!stream.CanSeek)
                throw new ArgumentException("The library requires a Stream object with the 'seek' capability available (the CanSeek flag). Please consider using the MemoryStream class.");
        }
        
        internal static ICollection<byte[]> GenerateImages(IDocument document)
        {
            var metadata = document.GetMetadata();
            var canvas = new ImageCanvas(metadata);
            RenderDocument(canvas, document);

            return canvas.Images;
        }

        internal static ICollection<PreviewerPicture> GeneratePreviewerPictures(IDocument document)
        {
            var canvas = new SkiaPictureCanvas();
            RenderDocument(canvas, document);
            return canvas.Pictures;
        }


        internal static void RenderDocument<TCanvas>(TCanvas canvas, IDocument document)
            where TCanvas : ICanvas, IRenderingCanvas
        {
            var container = new DocumentContainer();
            document.Compose(container);
            var content = container.Compose();

            // FontManagerに登録されているフォントファミリー名
            var registeredFontNames = FontManagerHelper.GetStyleSet()
                .Keys
                .OfType<string>()
                .ToList();
            
            var subsets = registeredFontNames.ToDictionary(name => name, _ => new StringBuilder());
            var dynamicSubsets = registeredFontNames.ToDictionary(name => name, _ => new StringBuilder());
            var suffix = Guid.NewGuid().ToString();
            var dynamicSuffix = Guid.NewGuid().ToString();

            // 1回目のApplyInheritedAndGlobalTexStyleの呼び出し
            // TextBlock のフォント名ごとにテキストを収集する。
            // DynamicHost にテキスト収集用の dictionary をセットする。この時 サブセットフォント名の suffix は指定しない。
            ApplyInheritedAndGlobalTexStyle(content, TextStyle.Default, subsets, suffix, dynamicSubsets, null);
            ApplyContentDirection(content, ContentDirection.LeftToRight);

            // Dynamic 要素以外のテキストを収集できたので、サブセットフォントを作成し登録する
            foreach (var name in subsets.Keys
                         .Where(name => subsets[name].Length != 0))
                FontManagerHelper.RegisterFont(name, subsets[name].ToString(), suffix);

            var debuggingState = Settings.EnableDebugging ? ApplyDebugging(content) : null;
            
            if (Settings.EnableCaching)
                ApplyCaching(content);

            var pageContext = new PageContext();

            // 1回目の呼び出しで、Dynamic 要素以外のサブセットフォントの登録は完了する。
            // この時、Dynamic 要素のテキストを収集する。
            // Dynamic 要素は、サブセットではない全てのグリフ入りのフォントを使い処理される。
            RenderPass(pageContext, new FreeCanvas(), content, debuggingState);
            
            // Dynamic 要素のテキストを収集できたので、サブセットフォントを作成し登録
            foreach (var name in dynamicSubsets.Keys
                         .Where(name => dynamicSubsets[name].Length != 0))
                FontManagerHelper.RegisterFont(name, dynamicSubsets[name].ToString(), dynamicSuffix);
            
            // 2回目のApplyInheritedAndGlobalTexStyleの呼び出し
            // DynamicHost にサブセットフォント名の suffix を指定することで、
            // RenderPass 呼び出し時にフォントファミリー名をsuffix付きのサブセットフォントにする。
            ApplyInheritedAndGlobalTexStyle(content, TextStyle.Default, null, null, dynamicSubsets, dynamicSuffix);

            // 2回目の呼び出しで、Dynamic 要素はサブセットフォントが使われる。
            RenderPass(pageContext, canvas, content, debuggingState);

            // サブセットフォントを削除
            var a = FontManagerHelper.GetStyleSet();
            FontManagerHelper.RemoveSubsetFontsBySuffix(suffix);
            FontManagerHelper.RemoveSubsetFontsBySuffix(dynamicSuffix);
        }
        
        internal static void RenderPass<TCanvas>(PageContext pageContext, TCanvas canvas, Container content, DebuggingState? debuggingState, bool draw = true)
            where TCanvas : ICanvas, IRenderingCanvas
        {
            InjectDependencies(content, pageContext, canvas);
            content.VisitChildren(x => (x as IStateResettable)?.ResetState());
            
            canvas.BeginDocument();

            var currentPage = 1;
            
            while(true)
            {
                pageContext.SetPageNumber(currentPage);
                debuggingState?.Reset();
                
                var spacePlan = content.Measure(Size.Max);

                if (spacePlan.Type == SpacePlanType.Wrap)
                {
                    canvas.EndDocument();
                    ThrowLayoutException();
                }

                try
                {
                    canvas.BeginPage(spacePlan);
                    content.Draw(spacePlan);
                }
                catch (Exception exception)
                {
                    canvas.EndDocument();
                    throw new DocumentDrawingException("An exception occured during document drawing.", exception);
                }

                canvas.EndPage();

                if (currentPage >= Settings.DocumentLayoutExceptionThreshold)
                {
                    canvas.EndDocument();
                    ThrowLayoutException();
                }
                
                if (spacePlan.Type == SpacePlanType.FullRender)
                    break;

                currentPage++;
            }
            
            canvas.EndDocument();

            void ThrowLayoutException()
            {
                var message = $"Composed layout generates infinite document. This may happen in two cases. " +
                              $"1) Your document and its layout configuration is correct but the content takes more than {Settings.DocumentLayoutExceptionThreshold} pages. " +
                              $"In this case, please increase the value {nameof(QuestPDF)}.{nameof(Settings)}.{nameof(Settings.DocumentLayoutExceptionThreshold)} static property. " +
                              $"2) The layout configuration of your document is invalid. Some of the elements require more space than is provided." +
                              $"Please analyze your documents structure to detect this element and fix its size constraints.";

                var elementTrace = debuggingState?.BuildTrace() ?? "Debug trace is available only in the DEBUG mode.";

                throw new DocumentLayoutException(message, elementTrace);
            }
        }

        internal static void InjectDependencies(this Element content, IPageContext pageContext, ICanvas canvas)
        {
            content.VisitChildren(x =>
            {
                if (x == null)
                    return;
                
                x.PageContext = pageContext;
                x.Canvas = canvas;
            });
        }

        private static void ApplyCaching(Container content)
        {
            content.VisitChildren(x =>
            {
                if (x is ICacheable)
                    x.CreateProxy(y => new CacheProxy(y));
            });
        }

        private static DebuggingState ApplyDebugging(Container content)
        {
            var debuggingState = new DebuggingState();

            content.VisitChildren(x =>
            {
                x.CreateProxy(y => new DebuggingProxy(debuggingState, y));
            });

            return debuggingState;
        }
        
        internal static void ApplyContentDirection(this Element? content, ContentDirection direction)
        {
            if (content == null)
                return;

            if (content is ContentDirectionSetter contentDirectionSetter)
            {
                ApplyContentDirection(contentDirectionSetter.Child, contentDirectionSetter.ContentDirection);
                return;
            }

            if (content is IContentDirectionAware contentDirectionAware)
                contentDirectionAware.ContentDirection = direction;
            
            foreach (var child in content.GetChildren())
                ApplyContentDirection(child, direction);
        }

        internal static void ApplyInheritedAndGlobalTexStyle(this Element? content, TextStyle documentDefaultTextStyle)
        {
            if (content == null)
                return;
            
            if (content is TextBlock textBlock)
            {
                foreach (var textBlockItem in textBlock.Items)
                {
                    if (textBlockItem is TextBlockSpan textSpan)
                        textSpan.Style = textSpan.Style.ApplyInheritedStyle(documentDefaultTextStyle).ApplyGlobalStyle();
                    
                    if (textBlockItem is TextBlockElement textElement)
                        ApplyInheritedAndGlobalTexStyle(textElement.Element, documentDefaultTextStyle);
                }
                
                return;
            }

            if (content is DynamicHost dynamicHost)
                dynamicHost.TextStyle = dynamicHost.TextStyle.ApplyInheritedStyle(documentDefaultTextStyle);
            
            if (content is DefaultTextStyle defaultTextStyleElement)
               documentDefaultTextStyle = defaultTextStyleElement.TextStyle.ApplyInheritedStyle(documentDefaultTextStyle);

            foreach (var child in content.GetChildren())
                ApplyInheritedAndGlobalTexStyle(child, documentDefaultTextStyle);
        }

        /// <summary>
        /// デフォルトのTextStyleを設定する。
        /// フォント名が指定されている場合、suffix を付けたサブセットフォント名に変更し、フォント名ごとに使われているテキストを収集する。
        /// </summary>
        /// <param name="content"></param>
        /// <param name="documentDefaultTextStyle"></param>
        /// <param name="subsets">テキスト収集のdictionary（Key: フォントファミリー名, Value: 使われているテキスト）</param>
        /// <param name="suffix">サブセットフォントのフォントファミリー名に付ける接尾辞</param>
        /// <param name="dynamicSubsets">DynamicHostに渡すdictionary</param>
        /// <param name="dynamicSuffix">DynamicHostに渡すサブセットフォントのフォントファミリー名に付ける接尾辞</param>
        /// <remarks>
        /// dictionaryにはあらかじめ登録済みのフォントファミリー名を格納しておくこと。
        /// 登録済みの場合のみテキストの収集とフォント名にsuffixを付ける。
        /// </remarks>
        internal static void ApplyInheritedAndGlobalTexStyle(this Element? content, TextStyle documentDefaultTextStyle, 
            Dictionary<string, StringBuilder>? subsets, 
            string? suffix,
            Dictionary<string, StringBuilder>? dynamicSubsets = null,
            string? dynamicSuffix = null)
        {
            if (content == null)
                return;
            
            if (content is TextBlock textBlock)
            {
                foreach (var textBlockItem in textBlock.Items)
                {
                    if (textBlockItem is TextBlockSpan textSpan)
                    {
                        textSpan.Style = textSpan.Style.ApplyInheritedStyle(documentDefaultTextStyle)
                            .ApplyGlobalStyle();
                        
                        // suffixを付けたサブセットフォント名に上書き
                        var name = textSpan.Style.FontFamily!;
                        if (suffix != null && subsets?.ContainsKey(name) == true)
                        {
                            var subsetFontName = FontSubsetter.GetSubsetFontFamilyName(name, suffix);
                            textSpan.Style = textSpan.Style.Mutate(TextStyleProperty.FontFamily, subsetFontName);
                        }

                        // フォント名ごとにテキストを収集
                        if (subsets != null && subsets.ContainsKey(name) && !string.IsNullOrEmpty(textSpan.Text)) 
                            subsets[name].Append(textSpan.Text);
                    }

                    if (textBlockItem is TextBlockElement textElement)
                        ApplyInheritedAndGlobalTexStyle(textElement.Element, documentDefaultTextStyle, subsets, suffix, dynamicSubsets, dynamicSuffix);
                }

                return;
            }

            if (content is DynamicHost dynamicHost)
            {
                dynamicHost.TextStyle = dynamicHost.TextStyle.ApplyInheritedStyle(documentDefaultTextStyle);

                // サブセット化のための情報を設定
                dynamicHost.Subsets = dynamicSubsets;
                dynamicHost.SubsetSuffix = dynamicSuffix;
            }

            if (content is DefaultTextStyle defaultTextStyleElement)
                documentDefaultTextStyle = defaultTextStyleElement.TextStyle.ApplyInheritedStyle(documentDefaultTextStyle);

            foreach (var child in content.GetChildren())
                ApplyInheritedAndGlobalTexStyle(child, documentDefaultTextStyle, subsets, suffix, dynamicSubsets, dynamicSuffix);
        }
    }
}