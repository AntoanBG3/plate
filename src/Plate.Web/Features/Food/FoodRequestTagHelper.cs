using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Plate.Web.Features.Food;

/// <summary>A new identifier on each render; no write is needed until the form is submitted.</summary>
[HtmlTargetElement("form", Attributes = "asp-food-request")]
public sealed class FoodRequestTagHelper : TagHelper
{
    public bool AspFoodRequest { get; set; } = true;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("asp-food-request");
        if (!AspFoodRequest) return;
        var input = new TagBuilder("input") { TagRenderMode = TagRenderMode.SelfClosing };
        input.Attributes["type"] = "hidden";
        input.Attributes["name"] = IdempotentFoodPostFilter.FieldName;
        input.Attributes["value"] = Guid.NewGuid().ToString();
        output.PostContent.AppendHtml(input);
    }
}
