# Reading the survey out of a worklist item

For whoever implements the MDVizio-X side. The bridge puts every answer the patient gave
into the worklist item (integration spec, section 5); this is the code that gets it back
out, and the one thing about it that surprises people.

## The surprise

Your reader has no dictionary entry for our private elements, so they arrive as **UN —
unknown bytes**, not as the LO and UT we sent. That is correct DICOM behaviour on both
sides and nothing is lost; the bytes are the value. But it means two things:

- `GetSingleValue<string>` on the JSON element throws. Decode the bytes as UTF-8 instead.
- The `(1001,xx40)` sequence arrives as an opaque blob. Parsing it needs the private
  dictionary registered. **Read the JSON element instead** — it holds the same answers and
  needs nothing registered. The sequence exists for readers that do register the tags.

Also: do not construct `DicomTag(0x1001, 0x0010)` and look it up. A received dataset keeps
private elements bound to the creator that reserved them, so a bare tag lookup can miss.
Walk the group.

## The code

fo-dicom, which is what both sides already use. Verified against a live bridge.

```csharp
/// <summary>The survey JSON from a worklist item, or null when there is none.</summary>
static string ReadSurveyJson(DicomDataset ds)
{
    const ushort group = 0x1001;
    const string creator = "MDAI_PRIVATE_CREATOR";

    // Find which block the creator reserved (PS3.5 §7.8.1) rather than assuming one.
    byte? block = null;
    foreach (var item in ds)
    {
        if (item.Tag.Group != group || item.Tag.Element > 0xFF) continue;
        if (Text(ds, item.Tag) == creator) { block = (byte)item.Tag.Element; break; }
    }
    if (block is not { } b) return null;

    var wanted = (ushort)((b << 8) | 0x31);          // the JSON element
    foreach (var item in ds)
        if (item.Tag.Group == group && item.Tag.Element == wanted)
            return Text(ds, item.Tag);

    return null;
}

/// <summary>Reads a value that may have arrived as unknown bytes.</summary>
static string Text(DicomDataset ds, DicomTag tag)
{
    try { return ds.GetSingleValue<string>(tag).TrimEnd('\0', ' '); }
    catch
    {
        try { return Encoding.UTF8.GetString(ds.GetValues<byte>(tag)).TrimEnd('\0', ' '); }
        catch { return null; }
    }
}
```

Then it is ordinary JSON:

```csharp
var json = ReadSurveyJson(worklistItem);
if (json is not null)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var symptoms = root.GetProperty("symptoms").EnumerateArray().Select(e => e.GetString());
    var contact  = root.GetProperty("tbInfo").GetProperty("closeContactActiveTB").GetString();
}
```

Output from a real run against the bridge:

    환자 bbbbbbbb-cccc-dddd-eeee-ffffffffffff  TAB-A3F2-0010
      JSON 1294 chars
      이름   Dela Cruz, Juan
      증상   COUGH_2WEEKS_PLUS, NIGHT_SWEATS
      SpO2   98.0
      접촉력 YES

## Parsing rules that will bite otherwise

- **An unanswered field is absent, not `null`.** Do not expect `"gender": null` — expect no
  `gender` key. Missing keys are the normal case; a survey is filled under time pressure.
- **Enums travel as the symbolic name**, never the display label and never a number:
  `COUGH_2WEEKS_PLUS`, `DONT_KNOW`, `FIVE_TO_10`. Treat an unrecognised value as "not
  answered" rather than failing the parse — the form gains options over time.
- `symptoms: ["NONE"]` is an explicit "no symptoms" answer. An empty array means the
  question was skipped. They are not the same.
- Section 9 of the integration spec has the full field list.

## If you would rather not parse anything

Two lighter options, both standard attributes that need no private handling at all:

| | |
|---|---|
| `(0010,21B0)` Additional Patient History | The survey as readable lines. Everything answered, laid out for a person |
| `(0008,1030)` / `(0032,1060)` / `(0040,0007)` | One line naming the findings that change a chest read — `TB scr: cough2w,nightsweats,contact` |

Those are already populated on every item. The JSON is for when the software wants the
answers as data rather than as text.
