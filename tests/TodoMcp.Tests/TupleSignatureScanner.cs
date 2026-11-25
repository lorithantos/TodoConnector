using System.Reflection;
using System.Runtime.CompilerServices;

namespace TodoMcp.Tests;

/// <summary>
/// Reflection over declared members, looking for ValueTuple anywhere in a type: house rule 5
/// (JanetHome note.csharp-house-rules), which no analyzer enforces. Copied from CardImport's
/// tests\CardImport.App.Tests\TupleSignatureScanner.cs, the reference implementation.
/// </summary>
public static class TupleSignatureScanner
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IReadOnlyList<string> Find(Assembly assembly)
    {
        List<string> found = new List<string>();
        foreach (Type type in assembly.GetTypes())
        {
            if (IsGenerated(type) || type == typeof(NoTupleSignaturesTests.TupleCarrier))
            {
                continue;
            }

            found.AddRange(FindIn(type));
        }

        return found;
    }

    public static IReadOnlyList<string> FindIn(Type type)
    {
        List<string> found = new List<string>();
        string owner = type.FullName ?? type.Name;

        foreach (MethodInfo method in type.GetMethods(Declared))
        {
            if (IsGenerated(method))
            {
                continue;
            }

            if (ContainsTuple(method.ReturnType))
            {
                found.Add($"{owner}.{method.Name} returns {method.ReturnType}");
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (ContainsTuple(parameter.ParameterType))
                {
                    found.Add($"{owner}.{method.Name} parameter {parameter.Name}");
                }
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(Declared))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                if (ContainsTuple(parameter.ParameterType))
                {
                    found.Add($"{owner} constructor parameter {parameter.Name}");
                }
            }
        }

        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            if (ContainsTuple(property.PropertyType))
            {
                found.Add($"{owner}.{property.Name} property");
            }
        }

        foreach (FieldInfo field in type.GetFields(Declared))
        {
            if (!IsGenerated(field) && ContainsTuple(field.FieldType))
            {
                found.Add($"{owner}.{field.Name} field");
            }
        }

        return found;
    }

    private static bool ContainsTuple(Type type)
    {
        if (type.HasElementType)
        {
            return ContainsTuple(type.GetElementType()!);
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        string? name = type.GetGenericTypeDefinition().FullName;
        if (name is not null && name.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            if (ContainsTuple(argument))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenerated(MemberInfo member)
    {
        return member.Name.Contains('<', StringComparison.Ordinal) ||
               member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
    }
}
