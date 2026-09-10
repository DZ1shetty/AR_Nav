# AR-NAV Unity Setup Guide
## Unity 6 + ARCore (No Vuforia)

---

## Step 1 — Open the Project in Unity

1. Open **Unity Hub**
2. Click **"Add project from disk"**
3. Navigate to: `d:\Programming Trash\Programming Trash\AR-NAV`
4. Click **Open**
5. Unity will auto-resolve packages from `manifest.json` — wait for it to finish importing (may take 3-5 minutes)

---

## Step 2 — Switch to Android Platform

1. Go to `File > Build Settings`
2. Select **Android** in the platform list
3. Click **Switch Platform** (wait for reimport)

---

## Step 3 — Player Settings (Critical for Redmi)

Go to `Edit > Project Settings > Player > Android tab`:

| Setting | Value |
|---|---|
| Minimum API Level | Android 7.0 (API 26) |
| Target API Level | Android 13 (API 33) |
| Graphics APIs | **OpenGL ES 3.0 ONLY** (remove Vulkan by clicking minus) |
| Scripting Backend | IL2CPP |
| Target Architecture | ARM64 (deselect ARMv7) |

---

## Step 4 — XR Settings

Go to `Edit > Project Settings > XR Plug-in Management`:

1. Click the **Android** tab
2. Check **ARCore** checkbox
3. Make sure "Initialize XR on Startup" is **DISABLED** (we init manually)

---

## Step 5 — Create the Main Scene

1. Go to `File > New Scene > Basic (URP)`
2. Delete the default `Main Camera` GameObject
3. In the **Hierarchy**, right-click → `XR > AR Session`
4. Right-click again → `XR > XR Origin (AR)`

You should now have:
```
Hierarchy:
├── AR Session
└── XR Origin (AR)
    └── Camera Offset
        └── Main Camera
```

---

## Step 6 — Add AR Tracked Image Manager

1. Select the **XR Origin (AR)** GameObject
2. In Inspector, click **Add Component** → search `AR Tracked Image Manager`
3. In the **Serialized Reference Image Library** field, click the circle and select (we create this next)

---

## Step 7 — Create the Reference Image Library

1. In the **Project** panel, go to `Assets/Textures/Markers`
2. You will see **`ISL07_DoorPhoto.jpg`** — this is the **actual photo of the ISL07 lab door** (the real one with the blue sign)
3. **Right-click in Project panel** → `Create > XR > Reference Image Library`
4. Name it `ISL07_ImageLibrary`
5. Click on the library → in Inspector, click **Add Image**
6. Drag **`ISL07_DoorPhoto.jpg`** into the slot
7. Set **Name** = `ISL07_DoorPhoto` (must match exactly — this is what `ImageTrackingController.cs` looks for)
8. Set **Physical Size** = `0.12, 0.05` (approx. 12cm × 5cm for the blue ISL07 sign label on the door)
9. Check **Keep Texture at Runtime**
10. Assign this library to the **AR Tracked Image Manager > Serialized Library** field

> **How it works in real life:**
> When you open the app and point your Redmi camera at the **actual ISL07 door**, ARCore matches what it sees against the stored photo. The distinctive blue sign + black door frame + white wall give it very strong feature points to lock onto. You do NOT need to print anything — just point at the real door.

---

## Step 8 — Create the Arrow Prefab

1. In Hierarchy, right-click → `Create Empty` → name it `ARArrowPrefab`
2. Add Component → `Arrow Mesh Builder` (the script we wrote)
3. Set the glow color to your preference (default: cyan)
4. Drag this GameObject from Hierarchy into `Assets/Prefabs` folder → it becomes a prefab
5. Delete it from the Hierarchy

---

## Step 9 — Create the Navigation Manager

1. Create Empty GameObject → name it `NavigationManager`
2. Add Component → `Image Tracking Controller`
3. Add Component → `Sequential Navigator`

In **Image Tracking Controller** Inspector:
- Navigator → drag `NavigationManager` (it has Sequential Navigator)
- HUD → assign later (step 10)
- Scan Panel → assign (step 10)
- Nav Panel → assign (step 10)

In **Sequential Navigator** Inspector:
- Arrow Prefab → drag `ARArrowPrefab` from Assets/Prefabs
- Reach Radius → `1.5`

---

## Step 10 — Create the Canvas UI

1. Right-click Hierarchy → `UI > Canvas`
2. Set Canvas → Screen Space Overlay

Create these child UI elements:
```
Canvas
├── ScanPanel (Panel)
│   └── Text: "Point camera at ISL07 door sign"
├── NavHUD (Panel, semi-transparent background)
│   ├── InstructionText (Text, large, centered top)
│   └── StepText (Text, smaller, below instruction)
└── ArrivalPanel (Panel, hidden by default)
    └── Text: "Cybersecurity Lab — You've Arrived! 🎉"
```

3. Assign these UI elements to `HUD Controller` and `Image Tracking Controller` scripts

---

## Step 11 — Add AndroidManifest.xml

Create file at: `Assets/Plugins/Android/AndroidManifest.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <uses-permission android:name="android.permission.CAMERA"/>
    <uses-feature android:name="android.hardware.camera.ar" android:required="true"/>
    <application>
        <meta-data android:name="com.google.ar.core" android:value="required"/>
    </application>
</manifest>
```

---

## Step 12 — Build APK

1. `File > Build Settings`
2. Click **Add Open Scenes** to add MainScene
3. Click **Build** → save as `AR-NAV.apk`
4. Install on Redmi: `adb install AR-NAV.apk`

---

## Testing Without Physical Walk

Right-click on `NavigationManager` in Hierarchy → select **"DEBUG: Simulate ISL07 Scan"** to trigger navigation without scanning a real marker. This lets you test arrow spawning sequence in the editor.
