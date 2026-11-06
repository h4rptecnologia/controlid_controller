using System.Globalization;
using System.Text;

namespace Platform.Modules.Importation;

public static class PreviewKinds
{
    public const string New = "new";
    public const string Update = "update";
    public const string NoChange = "no_change";
    public const string ExactDuplicate = "exact_duplicate";
    public const string PossibleDuplicate = "possible_duplicate";
    public const string FieldConflict = "field_conflict";
    public const string Invalid = "invalid";
}

public static class PreviewCodes
{
    public const string RegistrationRepeatedInFile = "registration_repeated_in_file";
    public const string AmbiguousMatch = "ambiguous_match";
    public const string RegistrationInOtherSite = "registration_in_other_site";
    public const string SameNameDifferentRegistration = "same_name_different_registration";
    public const string PersonRevoked = "person_revoked";
    public const string FieldDiffers = "field_differs";
    public const string MissingName = "missing_name";
    public const string MissingRegistration = "missing_registration";
    public const string InvalidDate = "invalid_date";
    public const string InvalidPersonType = "invalid_person_type";
    public const string UnknownUserType = "unknown_user_type";
}

public static class PersonFields
{
    public const string DisplayName = "display_name";
    public const string Registration = "registration";
    public const string PersonType = "person_type";
    public const string ValidFrom = "valid_from";
    public const string ValidTo = "valid_to";

    public static readonly IReadOnlyList<string> All = [DisplayName, Registration, PersonType, ValidFrom, ValidTo];
}

public static class FieldPolicies
{
    public const string PlatformWins = "platform_wins";
    public const string SourceWins = "device_wins";
    public const string Manual = "manual";

    public static bool IsKnown(string value) => value is PlatformWins or SourceWins or Manual;
}

/// <summary>Mascara deterministica para amostras: mostra o bastante para reconhecer, nunca o valor inteiro.</summary>
public static class Mask
{
    public static string Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part =>
        {
            var runes = part.EnumerateRunes().ToList();
            return runes[0] + new string('*', Math.Max(runes.Count - 1, 2));
        });
        return string.Join(' ', parts);
    }

    /// <summary>Ate dois caracteres no fim, e so se ao menos dois tercos ficarem cobertos.</summary>
    public static string Code(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var runes = value.EnumerateRunes().ToList();
        var visible = runes.Count >= 6 ? 2 : 0;
        var tail = new StringBuilder();
        foreach (var rune in runes.Skip(runes.Count - visible))
        {
            tail.Append(rune);
        }

        return new string('*', Math.Max(runes.Count - visible, 4)) + tail;
    }
}

/// <summary>Uma pessoa lida do arquivo. So os campos do cadastro: senha e biometria nem entram aqui.</summary>
public sealed record PersonCandidate(
    long Row,
    string? SourceId,
    string? Registration,
    string? DisplayName,
    string PersonType,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<string> Errors);

/// <param name="SameSite">Da unidade do arquivo ou sem unidade. As demais so sinalizam que a matricula existe em outro lugar.</param>
public sealed record ExistingPerson(
    Guid Id,
    bool SameSite,
    string DisplayName,
    string? Registration,
    string PersonType,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string State,
    string? LinkedSourceId);

public sealed record PreviewChange(string Field, string From, string To, string Policy);

public sealed record PreviewCandidate(Guid PersonId, string Name, string Registration);

public sealed record PreviewSample(
    string Name,
    string Registration,
    IReadOnlyList<PreviewChange> Changes,
    IReadOnlyList<PreviewCandidate> Candidates);

public sealed record PreviewRow(
    long Row,
    string? SourceKey,
    string Kind,
    Guid? MatchPersonId,
    IReadOnlyList<string> Codes,
    PreviewSample Sample);

/// <summary>
/// Conciliacao, na ordem de confianca do documento de importacao: vinculo com o equipamento, depois matricula na unidade.
/// Nome nunca e chave automatica: no maximo levanta suspeita. Empate exige decisao humana. Sem acesso a banco, para ser
/// testado sozinho.
/// </summary>
public static class PreviewClassifier
{
    public static IReadOnlyList<PreviewRow> Classify(
        IEnumerable<PersonCandidate> candidates,
        IReadOnlyCollection<ExistingPerson> existing,
        IReadOnlyDictionary<string, string> fieldPolicy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(fieldPolicy);

        var active = existing.Where(p => p.State != "deleted").ToList();
        var byLink = active.Where(p => p.LinkedSourceId is not null).ToLookup(p => p.LinkedSourceId!, StringComparer.Ordinal);
        var byRegistration = active.Where(p => p.Registration is not null).ToLookup(p => p.Registration!, StringComparer.Ordinal);
        var byName = active.Where(p => p.SameSite).ToLookup(p => NormalizeName(p.DisplayName), StringComparer.Ordinal);

        var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var seenRegistrations = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<PreviewRow>();

        foreach (var candidate in candidates)
        {
            rows.Add(ClassifyOne(candidate, byLink, byRegistration, byName, seenFingerprints, seenRegistrations, fieldPolicy));
        }

        return rows;
    }

    private static PreviewRow ClassifyOne(
        PersonCandidate candidate,
        ILookup<string, ExistingPerson> byLink,
        ILookup<string, ExistingPerson> byRegistration,
        ILookup<string, ExistingPerson> byName,
        HashSet<string> seenFingerprints,
        HashSet<string> seenRegistrations,
        IReadOnlyDictionary<string, string> fieldPolicy)
    {
        var codes = candidate.Errors.ToList();
        if (string.IsNullOrWhiteSpace(candidate.DisplayName) && !codes.Contains(NativeRowErrors.MissingRequired))
        {
            codes.Add(PreviewCodes.MissingName);
        }

        if (string.IsNullOrEmpty(candidate.Registration) && !codes.Contains(NativeRowErrors.MissingRequired))
        {
            codes.Add(PreviewCodes.MissingRegistration);
        }

        // Repeticao identica nao e erro de dado: o parser acusa a chave repetida, mas aqui ela vira duplicado exato.
        var onlyRepeatedKey = codes.All(c => c == NativeRowErrors.DuplicateKey);
        if (onlyRepeatedKey && !seenFingerprints.Add(Fingerprint(candidate)))
        {
            return Row(candidate, PreviewKinds.ExactDuplicate, null, []);
        }

        if (codes.Count > 0)
        {
            return Row(candidate, PreviewKinds.Invalid, null, codes);
        }

        var registration = candidate.Registration!;
        if (!seenRegistrations.Add(registration))
        {
            return Row(candidate, PreviewKinds.PossibleDuplicate, null, [PreviewCodes.RegistrationRepeatedInFile]);
        }

        var linked = candidate.SourceId is null ? [] : byLink[candidate.SourceId].Where(p => p.SameSite).ToList();
        var sameRegistration = byRegistration[registration].ToList();
        var matches = linked.Concat(sameRegistration.Where(p => p.SameSite)).DistinctBy(p => p.Id).ToList();

        if (matches.Count > 1)
        {
            return Row(candidate, PreviewKinds.PossibleDuplicate, null, [PreviewCodes.AmbiguousMatch], candidates: matches);
        }

        if (matches.Count == 1)
        {
            return Compare(candidate, matches[0], fieldPolicy);
        }

        // Em outra unidade nao vira candidato: o operador pode nem ter acesso a ela, entao nao mostra quem e.
        if (sameRegistration.Count > 0)
        {
            return Row(candidate, PreviewKinds.PossibleDuplicate, null, [PreviewCodes.RegistrationInOtherSite]);
        }

        var sameName = byName[NormalizeName(candidate.DisplayName!)].ToList();
        return sameName.Count > 0
            ? Row(candidate, PreviewKinds.PossibleDuplicate, null, [PreviewCodes.SameNameDifferentRegistration], candidates: sameName)
            : Row(candidate, PreviewKinds.New, null, []);
    }

    private static PreviewRow Compare(PersonCandidate candidate, ExistingPerson person, IReadOnlyDictionary<string, string> fieldPolicy)
    {
        var changes = new List<PreviewChange>();
        Diff(PersonFields.DisplayName, candidate.DisplayName!.Trim(), person.DisplayName.Trim(), Mask.Name);
        Diff(PersonFields.Registration, candidate.Registration, person.Registration, Mask.Code);
        Diff(PersonFields.PersonType, candidate.PersonType, person.PersonType, static v => v ?? string.Empty);
        Diff(PersonFields.ValidFrom, Stamp(candidate.ValidFrom), Stamp(person.ValidFrom), static v => v ?? string.Empty);
        Diff(PersonFields.ValidTo, Stamp(candidate.ValidTo), Stamp(person.ValidTo), static v => v ?? string.Empty);

        if (person.State == "revoked")
        {
            // Reimportar nao pode desfazer revogacao em silencio, mesmo com os campos iguais.
            return Row(candidate, PreviewKinds.FieldConflict, person.Id, [PreviewCodes.PersonRevoked], changes);
        }

        var effective = changes.Where(c => c.Policy != FieldPolicies.PlatformWins).ToList();
        if (effective.Count == 0)
        {
            return Row(candidate, PreviewKinds.NoChange, person.Id, [], changes);
        }

        return effective.Any(c => c.Policy == FieldPolicies.Manual)
            ? Row(candidate, PreviewKinds.FieldConflict, person.Id, [PreviewCodes.FieldDiffers], changes)
            : Row(candidate, PreviewKinds.Update, person.Id, [], changes);

        void Diff(string field, string? incoming, string? current, Func<string?, string> mask)
        {
            if (!string.Equals(incoming ?? string.Empty, current ?? string.Empty, StringComparison.Ordinal))
            {
                // Padrao conservador: o que nao foi escolhido pede decisao de uma pessoa.
                var policy = fieldPolicy.GetValueOrDefault(field) ?? FieldPolicies.Manual;
                changes.Add(new PreviewChange(field, mask(current), mask(incoming), policy));
            }
        }
    }

    private static PreviewRow Row(
        PersonCandidate candidate,
        string kind,
        Guid? match,
        IReadOnlyList<string> codes,
        IReadOnlyList<PreviewChange>? changes = null,
        IReadOnlyList<ExistingPerson>? candidates = null) =>
        new(
            candidate.Row,
            IsSafeKey(candidate.SourceId) ? candidate.SourceId : null,
            kind,
            match,
            codes,
            new PreviewSample(
                Mask.Name(candidate.DisplayName),
                Mask.Code(candidate.Registration),
                changes ?? [],
                (candidates ?? []).Select(p => new PreviewCandidate(p.Id, Mask.Name(p.DisplayName), Mask.Code(p.Registration))).ToList()));

    /// <summary>Chave so entra na previa se for numero: em linha invalida, ela pode ser qualquer texto, inclusive dado pessoal.</summary>
    public static bool IsSafeKey(string? value) =>
        value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit);

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLower(CultureInfo.InvariantCulture);

    private static string? Stamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Fingerprint(PersonCandidate c) =>
        string.Join('\u001f', c.SourceId, c.Registration, c.DisplayName?.Trim(), c.PersonType, Stamp(c.ValidFrom), Stamp(c.ValidTo));
}

/// <summary>Como a aprovacao resume a execucao. Linha sem decisao em categoria que exige decisao bloqueia o envio.</summary>
public static class PreviewPlan
{
    public static bool RequiresDecision(string kind) => kind is PreviewKinds.PossibleDuplicate or PreviewKinds.FieldConflict;

    /// <summary>Se a decisao vale para a categoria. Vincular so existe onde ha candidatos; manter e aceitar, onde ha divergencia.</summary>
    public static bool Allows(string kind, string decision) => (kind, decision) switch
    {
        (PreviewKinds.FieldConflict, ImportDecisions.AcceptSource or ImportDecisions.KeepPlatform or ImportDecisions.Skip) => true,
        (PreviewKinds.PossibleDuplicate, ImportDecisions.Link or ImportDecisions.CreateNew or ImportDecisions.Skip) => true,
        _ => false,
    };
}

public static class ImportDecisions
{
    public const string AcceptSource = "accept_source";
    public const string KeepPlatform = "keep_platform";
    public const string Link = "link";
    public const string CreateNew = "create_new";
    public const string Skip = "skip";
}
