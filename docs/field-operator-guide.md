# MedMission — Field Operator Guide

For the person running the laptop at a screening site. Print this and keep it with the
laptop. You do not need to know anything about the software beyond these steps.

The laptop is the centre of everything: tablets send surveys to it, and the X-ray
console reads its worklist. **If the laptop is off, tablets keep working** — they hold
the surveys and send them once the laptop is back.

---

## 1. Start the laptop (once, each morning)

1. Turn on the laptop and connect it to the site Wi-Fi — the same network the tablets use.
2. Open the `MedMissionBridge` folder and run `MedMissionBridge.exe`. A black window opens
   and stays open. **Do not close it.** Minimise it instead.
3. Open a browser and go to **http://127.0.0.1:18080/**. This page is the control panel.
   Leave it open all day.

## 2. Read the page before the first patient

At the top of the page:

- **Lines marked ⚠** — act on them now. Each one says what to do. The two you may see are
  a worklist port that Windows has reserved (the line names the port to switch to) and a
  network address that may be wrong for this site. If you cannot resolve one, call
  technical support before opening — the surveys will still be collected either way, but
  the X-ray console may not see the patients.
- **Tablet API key** — a code like `C79QS-CQ8RM-5QRWU-ABDEE`. You type this into every
  tablet, once. It is different on every laptop. If you replace the laptop, every tablet
  needs the new laptop's key.

## 3. Set up each tablet (once per tablet, per laptop)

1. Connect the tablet to the same Wi-Fi.
2. Open the MedMission app, tap **+ New Survey**, then scroll down and tap **Done**.
   (You are not saving a real patient — this is just the way to reach the laptop list.)
3. The laptop should appear under **Discovered Laptops**. Tap **Add**.
   - If it never appears, use **Add a laptop manually** and enter the laptop's address.
     The address is on the laptop page, in the line about what tablets are told to
     connect to. The port is `18080`.
4. In the laptop's card, type the **API key** exactly as shown on the laptop page.
   It is case-sensitive. Tap **Save key**.
5. Tap **Send**. The record should turn **SENT**, and it should appear in the list on the
   laptop page within a second or two. That confirms the tablet is ready.

Repeat for every tablet. After this, staff just fill surveys and send.

## 4. During the day

- Surveys arrive on the laptop page as **Received**. They are on the X-ray console's
  worklist from that moment.
- When a patient is being x-rayed, set the record to **InProgress**; when the image is
  taken, set it to **Completed**. **This is your job — nothing does it automatically.**
  A record left as Received stays on the console's worklist all day.
- **Cancelled** is for a patient who left or was entered twice.
- Completed and Cancelled records disappear from the console's worklist but stay in the
  list on your page.

## 5. When something goes wrong

| What you see | What to do |
|---|---|
| Tablet says **"Laptop rejected the API key"** | The key is wrong or the laptop was replaced. Re-read the key on the laptop page and type it into that tablet again. |
| Tablet says **"Send failed — will retry automatically"** | Laptop is off, asleep, or off the Wi-Fi. Fix that; the tablet resends by itself. Nothing is lost. |
| Record stuck on **PENDING** | Same as above — it is waiting, not broken. |
| Record shows **FAILED** | Open the record, scroll down, tap **Done**, pick the laptop, tap **Send** again. |
| Tablet cannot find the laptop | Check both are on the same Wi-Fi. Then add the laptop manually using the address from the laptop page. |
| Laptop page shows **⚠ modality worklist not running** | The line names a port to use. Change it as described, restart the exe, and tell the X-ray team the new port. Surveys keep arriving meanwhile. |
| Laptop page will not load | The black window was closed. Run `MedMissionBridge.exe` again. |
| A laptop appears twice in the tablet's list | Use the one that has the API key filled in. Newer app versions prevent this. |

## 6. End of day — back up

Everything the site collected today is on this laptop and nowhere else. A lost or broken
laptop is lost patient data. Before you shut down:

1. On the laptop page, press **Back up database**. It writes a file and shows you where.
2. Copy that file to a USB drive. The folder is
   `C:\ProgramData\MedMissionBridge\backups\` and the file is named by the date and
   time, newest last.

Do not copy the database while the bridge is running instead — a copy taken mid-write can
be unreadable. The button exists so you get a copy that is guaranteed to open. The laptop
keeps the last 14 backups and deletes older ones; anything you put in that folder yourself
is left alone.

Then close the black window and turn the laptop off. No particular order is needed.

---

**Support:** note the record number (like `TAB-A3F2-0007`) and what the screen said. Both
the laptop page and the tablet show enough to identify any single patient's record.
