using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Semver.Ranges.Npm
{
    internal class ComparatorParser
    {
        private enum VersionRoundingType
        {
            Zero,
            ClosestCompatible,
            ReasonablyClose,
        }

        // Note: Order matters for key-values with the same value.
        // Only the first instance will be added to OperatorsReverse, which is used for converting a comparator to a string.
        // Therefore, you should add the default value for an operator first.
        internal static readonly IDictionary<string, ComparatorOp> Operators = new Dictionary<string, ComparatorOp>
        {
            { "<", ComparatorOp.LessThan },
            { ">", ComparatorOp.GreaterThan },
            { "<=", ComparatorOp.LessThanOrEqualTo },
            { ">=", ComparatorOp.GreaterThanOrEqualTo },
            { "^", ComparatorOp.CompatibleWith },
            { "~", ComparatorOp.ReasonablyClose },
            { "~>", ComparatorOp.ReasonablyClose }, // TODO should the `~>` synonym for `~` be supported?
            { "", ComparatorOp.Equals }, // Equals is implied when there's no operator prefix and all numbers are defined
            { "=", ComparatorOp.Equals },
        };

        // Holds the reverse of Operators. If there's multiple keys with the same value
        // only the first instance will be added to this dictionary.
        internal static readonly IDictionary<ComparatorOp, string> OperatorsReverse;

        internal static readonly SemVersion ZeroVersion = new SemVersion(0, 0, 0);
        internal static readonly SemVersion ZeroVersionWithPrerelease = SemVersion.ParsedFrom(0, 0, 0, "0");

        static ComparatorParser()
        {
            OperatorsReverse = new Dictionary<ComparatorOp, string>();

            foreach (var kv in Operators)
            {
                if (!OperatorsReverse.ContainsKey(kv.Value))
                    OperatorsReverse.Add(kv.Value, kv.Key);
            }
        }

        public static IEnumerable<NpmComparator> ParseComparators(string range, bool includeAllPrerelease)
        {
            if (range.Length == 0) // Empty ranges imply *
            {
                yield return new NpmComparator(includeAllPrerelease);
                yield break;
            }

            var hyphenMatch = RangeRegex.HyphenRange.Match(range);
            if (hyphenMatch.Success)
            {
                ParseHyphenRange(hyphenMatch, includeAllPrerelease, out var minComp, out var maxComp);

                yield return minComp;
                yield return maxComp;

                yield break;
            }

            if (RangeRegex.OperatorRangeTest.IsMatch(range))
            {
                var operatorRanges = RangeRegex.OperatorRange.Matches(range);
                foreach (Match rangeMatch in operatorRanges)
                {
                    var comps = ParseOperatorRange(rangeMatch, includeAllPrerelease);

                    foreach (var comp in comps)
                        yield return comp;
                }

                yield break;
            }

            throw new FormatException($"Unknown range syntax: {range}");
        }

        private static void ParseHyphenRange(Match match, bool includeAllPrerelease, out NpmComparator minComparator, out NpmComparator maxComparator)
        {
            ParseVersion(match.Groups["minVersion"].Value, out var minMajor, out var minMinor, out var minPatch, out string minPrerelease, out string minMetadata);
            ParseVersion(match.Groups["maxVersion"].Value, out var maxMajor, out var maxMinor, out var maxPatch, out string maxPrerelease, out string maxMetadata);

            if (minMajor == null)
                minComparator = new NpmComparator(includeAllPrerelease);
            else
            {
                RoundVersion(VersionRoundingType.Zero, ref minMajor, ref minMinor, ref minPatch);

                if (includeAllPrerelease && string.IsNullOrEmpty(minPrerelease))
                    minPrerelease = "0";

                var minVersion = SemVersion.ParsedFrom(minMajor.Value, minMinor.Value, minPatch.Value, minPrerelease, minMetadata);
                minComparator = new NpmComparator(ComparatorOp.GreaterThanOrEqualTo, minVersion, includeAllPrerelease);
            }

            if (maxMajor == null)
                maxComparator = new NpmComparator(includeAllPrerelease);
            else
            {
                var op = ComparatorOp.LessThanOrEqualTo;

                if (maxMinor == null || maxPatch == null)
                {
                    if (maxMinor == null && maxPatch == null || maxMinor == null)
                        RoundVersion(VersionRoundingType.ClosestCompatible, ref maxMajor, ref maxMinor, ref maxPatch);
                    else
                        RoundVersion(VersionRoundingType.ReasonablyClose, ref maxMajor, ref maxMinor, ref maxPatch);

                    op = ComparatorOp.LessThan;
                    maxPrerelease = "0";
                }
                else if (includeAllPrerelease && maxPrerelease != "0")
                {
                    maxPatch += 1;
                    maxPrerelease = "0";
                    op = ComparatorOp.LessThan;
                }

                if (includeAllPrerelease && string.IsNullOrEmpty(maxPrerelease))
                    maxPrerelease = "0";

                var maxVersion = SemVersion.ParsedFrom(maxMajor.Value, maxMinor.Value, maxPatch.Value, maxPrerelease, maxMetadata);
                maxComparator = new NpmComparator(op, maxVersion, includeAllPrerelease);
            }
        }

        private static IEnumerable<NpmComparator> ParseOperatorRange(Match match, bool includeAllPrerelease)
        {
            string strOperator = match.Groups["operator"].Value;

            if (!Operators.TryGetValue(strOperator, out ComparatorOp op))
                op = ComparatorOp.Equals;

            ParseVersion(match, out var major, out var minor, out var patch, out var prerelease, out var metadata);

            if (major == null)
            {
                if (op == ComparatorOp.GreaterThan || op == ComparatorOp.LessThan)
                {
                    yield return new NpmComparator(ComparatorOp.LessThan, ZeroVersionWithPrerelease, includeAllPrerelease);
                    yield break;
                }

                yield return new NpmComparator(includeAllPrerelease);
                yield break;
            }

            if (op == ComparatorOp.Equals && minor != null && patch != null)
            {
                var semVersion = SemVersion.ParsedFrom(major.Value, minor.Value, patch.Value, prerelease, metadata);
                yield return new NpmComparator(ComparatorOp.Equals, semVersion, includeAllPrerelease);
                yield break;
            }

            if (op == ComparatorOp.GreaterThanOrEqualTo || op == ComparatorOp.GreaterThan)
            {
                if (op == ComparatorOp.GreaterThan)
                {
                    bool minorOrPatchNull = minor == null || patch == null;

                    if (minor == null)
                        RoundVersion(VersionRoundingType.ClosestCompatible, ref major, ref minor, ref patch);
                    else if (patch == null)
                        RoundVersion(VersionRoundingType.ReasonablyClose, ref major, ref minor, ref patch);

                    if (minorOrPatchNull)
                    {
                        op = ComparatorOp.GreaterThanOrEqualTo;

                        if (includeAllPrerelease)
                        {
                            prerelease = "0";
                            metadata = "";
                        }
                    }
                }
                else
                {
                    bool minorOrPatchNull = minor == null || patch == null;
                    RoundVersion(VersionRoundingType.Zero, ref major, ref minor, ref patch);

                    if (minorOrPatchNull && includeAllPrerelease)
                    {
                        prerelease = "0";
                        metadata = "";
                    }
                }

                var version = SemVersion.ParsedFrom(major.Value, minor.Value, patch.Value, prerelease, metadata);
                yield return new NpmComparator(op, version, includeAllPrerelease);
                yield break;
            }

            if (op == ComparatorOp.LessThanOrEqualTo || op == ComparatorOp.LessThan)
            {
                if (op == ComparatorOp.LessThan)
                {
                    bool minorOrPatchNull = minor == null || patch == null;

                    RoundVersion(VersionRoundingType.Zero, ref major, ref minor, ref patch);

                    if (minorOrPatchNull)
                    {
                        prerelease = "0";
                        metadata = "";
                    }
                }
                else
                {
                    if (minor == null || patch == null)
                    {
                        if (minor == null)
                            RoundVersion(VersionRoundingType.ClosestCompatible, ref major, ref minor, ref patch);
                        else
                            RoundVersion(VersionRoundingType.ReasonablyClose, ref major, ref minor, ref patch);

                        op = ComparatorOp.LessThan;
                        prerelease = "0";
                    }
                }

                var version = SemVersion.ParsedFrom(major.Value, minor.Value, patch.Value, prerelease, metadata);
                yield return new NpmComparator(op, version, includeAllPrerelease);
                yield break;
            }

            if (op == ComparatorOp.CompatibleWith || op == ComparatorOp.ReasonablyClose)
            {
                int? minMajor = major, minMinor = minor, minPatch = patch;
                RoundVersion(VersionRoundingType.Zero, ref minMajor, ref minMinor, ref minPatch);

                int? maxMajor = major, maxMinor = minor, maxPatch = patch;
                RoundVersion(op == ComparatorOp.CompatibleWith ? VersionRoundingType.ClosestCompatible : VersionRoundingType.ReasonablyClose, ref maxMajor, ref maxMinor, ref maxPatch);

                var minVersion = SemVersion.ParsedFrom(minMajor.Value, minMinor.Value, minPatch.Value, prerelease, metadata);
                var maxVersion = SemVersion.ParsedFrom(maxMajor.Value, maxMinor.Value, maxPatch.Value, "0");

                if (minVersion.ComparePrecedenceTo(ZeroVersion) != 0)
                    yield return new NpmComparator(ComparatorOp.GreaterThanOrEqualTo, minVersion, includeAllPrerelease);

                yield return new NpmComparator(ComparatorOp.LessThan, maxVersion, includeAllPrerelease);

                yield break;
            }

            // Partial version that expands into multiple comparators, for example "1.x" or "0.1.x" or "1"
            {
                int? minMajor = major, minMinor = minor, minPatch = patch;
                RoundVersion(VersionRoundingType.Zero, ref minMajor, ref minMinor, ref minPatch);

                int? maxMajor = major, maxMinor = minor, maxPatch = patch;
                // 0.0.x rounds to next minor version and 0.x.x rounds to next major version, regardless of whether major is 0 or not.
                if (minor == null)
                    RoundVersion(VersionRoundingType.ClosestCompatible, ref maxMajor, ref maxMinor, ref maxPatch);
                else
                    RoundVersion(VersionRoundingType.ReasonablyClose, ref maxMajor, ref maxMinor, ref maxPatch);

                var minVersion = SemVersion.ParsedFrom(minMajor.Value, minMinor.Value, minPatch.Value, includeAllPrerelease ? "0" : "");
                var maxVersion = SemVersion.ParsedFrom(maxMajor.Value, maxMinor.Value, maxPatch.Value, "0");

                if (minVersion.ComparePrecedenceTo(ZeroVersion) != 0)
                    yield return new NpmComparator(ComparatorOp.GreaterThanOrEqualTo, minVersion, includeAllPrerelease);

                yield return new NpmComparator(ComparatorOp.LessThan, maxVersion, includeAllPrerelease);
            }
        }

        private static void ParseVersion(string version, out int? major, out int? minor, out int? patch, out string prerelease, out string metadata)
        {
            var match = RangeRegex.PartialVersion.Match(version);
            ParseVersion(match, out major, out minor, out patch, out prerelease, out metadata);
        }

        private static void ParseVersion(Match match, out int? major, out int? minor, out int? patch, out string prerelease, out string metadata)
        {
            major = TryParseInt(match.Groups["major"].Value);
            minor = TryParseInt(match.Groups["minor"].Value);
            patch = TryParseInt(match.Groups["patch"].Value);
            prerelease = match.Groups["prerelease"].Value;
            metadata = match.Groups["metadata"].Value;
        }

        private static bool RoundVersion(VersionRoundingType roundingType, ref int? major, ref int? minor, ref int? patch)
        {
            if (major == null)
                throw new ArgumentException("Major can not be null");

            bool minorNull = minor == null;
            bool patchNull = patch == null;

            // Special case where ~0 or ~1 etc increments major version always
            if (roundingType == VersionRoundingType.ReasonablyClose && minor == null)
            {
                major += 1;
                minor = 0;
                patch = 0;
                return true;
            }

            bool changed = false;

            if (minorNull)
            {
                minor = 0;
                patch = 0;
            }
            else if (patchNull)
            {
                patch = 0;
            }

            switch (roundingType)
            {
                case VersionRoundingType.Zero: return false;
                case VersionRoundingType.ClosestCompatible:
                {
                    if (major == 0)
                    {
                        if (minorNull && patchNull)
                        {
                            major = 1;
                            minor = 0;
                            patch = 0;
                            changed = true;
                            break;
                        }

                        if (patchNull)
                        {
                            minor += 1;
                            patch = 0;
                            changed = true;
                            break;
                        }

                        if (major == 0 && minor == 0) // 0.0.1 --> 0.0.2
                        {
                            patch += 1;
                            changed = true;
                            break;
                        }

                        if (major == 0 && minor > 0) // 0.1.x --> 0.2.0
                        {
                            minor += 1;
                            patch = 0;
                            changed = true;
                            break;
                        }
                    }

                    major += 1;
                    minor = 0;
                    patch = 0;
                    changed = true;

                    break;
                }
                case VersionRoundingType.ReasonablyClose:
                {
                    minor += 1;
                    patch = 0;
                    changed = true;

                    break;
                }
            }

            return changed;
        }

        private static int? TryParseInt(string strInteger)
        {
            if (!int.TryParse(strInteger, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                return null;

            return result;
        }
    }
}
