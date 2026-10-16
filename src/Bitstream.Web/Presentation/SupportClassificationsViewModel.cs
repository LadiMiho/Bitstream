using Bitstream.Application.Services.PostActivation;

namespace Bitstream.Web.Presentation;

/// <summary>
/// The classifications screen: the whole tree plus the level-1 and level-2 items selected in the
/// first two columns (from the query string, so a reload after a change keeps the selection).
/// </summary>
public sealed record SupportClassificationsViewModel(
    IReadOnlyList<SupportClassificationNode> Tree,
    SupportClassificationNode? SelectedLevel1,
    SupportClassificationNode? SelectedLevel2)
{
    public static SupportClassificationsViewModel Create(IReadOnlyList<SupportClassificationNode> tree, long? level1Id, long? level2Id)
    {
        var level1 = tree.FirstOrDefault(n => n.Classification.ClassificationId == level1Id) ?? tree.FirstOrDefault();
        var level2 = level1?.Children.FirstOrDefault(n => n.Classification.ClassificationId == level2Id) ?? level1?.Children.FirstOrDefault();

        return new SupportClassificationsViewModel(tree, level1, level2);
    }
}

/// <summary>What the add drawer needs: the parent to add under (null for level 1) and a suggested code and order.</summary>
public sealed record SupportClassificationAddViewModel(
    Bitstream.Domain.Entities.SupportClassification? Parent,
    string SuggestedCode,
    int SuggestedDisplayOrder);

/// <summary>
/// One column of the classifications screen. <paramref name="SelectHref"/> is set for the first two
/// levels, whose rows select what the next column lists; <paramref name="CanAdd"/> is false when
/// there is no parent to add under yet.
/// </summary>
public sealed record SupportClassificationColumnModel(
    int Level,
    string Title,
    string Hint,
    Bitstream.Domain.Entities.SupportClassification? Parent,
    bool CanAdd,
    IReadOnlyList<SupportClassificationNode> Items,
    long? SelectedId,
    Func<long, string>? SelectHref,
    EmptyStateModel Empty);

/// <summary>The code, name and order inputs shared by the add and edit drawers.</summary>
public sealed record SupportClassificationFieldsModel(string IdPrefix, string Code, string Name, int DisplayOrder);
