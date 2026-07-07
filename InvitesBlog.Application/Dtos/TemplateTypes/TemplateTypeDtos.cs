namespace InvitesBlog.Application.Dtos.TemplateTypes;

public sealed record TemplateTypeDto(Guid Id, string Name, string Slug, int SortOrder, bool IsActive);

public sealed record CreateTemplateTypeRequest(string Name);
