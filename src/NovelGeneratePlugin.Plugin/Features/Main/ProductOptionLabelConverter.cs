using System.Globalization;
using Avalonia.Data.Converters;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;
/// <summary>领域枚举保持稳定序列化值，界面使用作者可理解的中文名称。</summary>
public sealed class ProductOptionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        WritingRuleKind.ForbiddenText => "禁止文本",
        WritingRuleKind.RequiredText => "必须包含",
        WritingRuleKind.Guidance => "语义指导（待模型检查）",
        WritingRuleScope.CommonSnapshot => "通用规则的本书快照",
        WritingRuleScope.Book => "本书",
        WritingRuleScope.Volume => "当前章节所属卷",
        WritingRuleScope.Run => "当前活动运行",
        WritingRuleStrength.Hard => "硬规则",
        WritingRuleStrength.Advisory => "建议",
        ManuscriptVersion.Editing => "编辑稿",
        ManuscriptVersion.WorkingView => "工作稿视图",
        ManuscriptVersion.Formal => "正式稿",
        StoryEntityKind.Person => "人物",
        StoryEntityKind.Place => "地点",
        StoryEntityKind.Item => "物品",
        StoryEntityKind.Organization => "组织",
        ManuscriptFormat.Text => "TXT 文本",
        ManuscriptFormat.Markdown => "Markdown",
        _ => "未选择"
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
