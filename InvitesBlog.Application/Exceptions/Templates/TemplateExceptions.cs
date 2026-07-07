namespace InvitesBlog.Application.Exceptions.Templates;

public sealed class TemplateNotFoundException(string identifier)
    : NotFoundException($"Template '{identifier}' was not found.", "template_not_found");
