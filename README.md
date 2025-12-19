# **ElevenLabs Unity Setup Package**

===========================================================================



This package provides:

\- A ready-to-use ElevenLabs Agent prefab

\- Push-To-Talk voice input

\- Debug UI

\- A sample scene for fast setup in new projects



**----------------------------------------------**

### **REQUIREMENTS**

**----------------------------------------------**



Before importing this package, install:



**1)** NativeWebSocket

   https://github.com/endel/NativeWebSocket.git#upm



**2)** Starter Assets – Third Person Controller

   [https://assetstore.unity.com/packages/essentials/starter-assets-thirdperson-updates-in-new-charactercontroller-pa-196526](https://assetstore.unity.com/packages/essentials/starter-assets-thirdperson-updates-in-new-charactercontroller-pa-196526)



**----------------------------------------------------**

##### **IMPORTANT NOTE ABOUT INPUT SYSTEM**

**----------------------------------------------------**



\- If you use the OLD Input System:

  You can leave all InputAction fields empty in the Inspector.



\- If you use the NEW Input System:

  You must create and assign Input Actions (steps below).



**------------------------------------------------------------**

###### **OPTION A — Use the Sample Scene (Recommended)**

**------------------------------------------------------------**



**1)** Open the sample scene:

&nbsp;  Open Package Manager -> select the package -> Samples -> Import “SampleScene”

   Then open:

&nbsp;		Assets/Samples/ElevenLabsQuickSetup/<version>/SampleScene/Scenes/ElevenLabsStarterAssetSampleScene.unity



**2)** Select Agent/ElevenLabsAgent object i Hierachy

 	- In the Inspector (ElevenLabsAgent(Script) component), confirm API Key + AgentId 

&nbsp;	- Update if needed



**3)** Create Input Actions (New Input System only)



   Open:

     Assets/StarterAssets/InputSystem/StarterAssets.inputactions



   In the "Player" action map, add:



   - PushToTalk

     Type: Button

     Binding: Keyboard -> T



   - ToggleDebugUI

     Type: Button

     Binding: Keyboard -> Tab



   Save the Input Actions asset.



**4)** Assign Input Actions in the scene



   Push-To-Talk:

   - Select: Agent -> ElevenLabsAgent

   - Assign:

     Push To Talk Action = Player / PushToTalk



   Debug UI:

   - Select: DebugUI

   - Assign:

     Toggle Debug Action = Player / ToggleDebugUI



**5)** Press Play



**-------------------------------------------------------**

###### **OPTION B — Add to an Existing Player Setup**

**-------------------------------------------------------**



**1)** Add the Agent prefab

   - Drag ElevenLabsAgent into the scene

   - Parent it to your character or agent root

   - Adjust position/rotation if needed

   - In the Inspector (ElevenLabsAgent(Script) component), confirm API Key + AgentId

   - Update if needed





**2)** Add the Debug UI

   - Drag DebugUI prefab into the scene



**3)** Create Input Actions (New Input System only)

   - PushToTalk (Keyboard T)

   - ToggleDebugUI (Keyboard Tab)



**4)** Assign Input Actions

   - ElevenLabsAgent -> PushToTalk

   - DebugUI -> ToggleDebugUI



**5)** Press Play



**------------------------------------------------------------**

###### **IMPORTANT: CHECK AUDIO SAMPLE RATE (FIRST RUN)**

**------------------------------------------------------------**

**Why this matters:**

If the sample rate is wrong, audio will sound pitched (chipmunk/deep) and/or sped up / slowed down.



*ElevenLabs reports which audio formats it expects for:*

\- agent\_output\_audio\_format (what the agent sends back)

\- user\_input\_audio\_format (what the agent expects from your mic)



On first connect, check the Unity Console / Output Log for a line like:



\[ElevenLabs] INIT METADATA: ... "agent\_output\_audio\_format":"pcm\_48000", "user\_input\_audio\_format":"pcm\_48000"



Then make sure your Inspector values match:



**1)** VoiceInputManager (Sample Rate)

   - Set to the same rate as user\_input\_audio\_format (example: 48000)



If the sample rates do NOT match, you can get:

\- chipmunk / deep voice sound

\- sped up / slowed down audio

\- weird artifacts or stutter



How this package works (important):

This package uses ONE sample rate setting as the "source of truth":

\- VoiceInputManager -> Sample Rate



That value is used for:

\- microphone capture / sending audio to ElevenLabs

\- playback of received PCM audio (AudioOutputManager is called using VoiceInputManager.sampleRate)





**----------------------------------------------**

###### **DEVELOPER NOTES**

**----------------------------------------------**





\- Push-To-Talk does NOT use Input System "Hold" interactions

\- Micro-click filtering is handled in code

\- Package supports both:

  - New Input System (recommended)

  - Old Input System (KeyCode fallback)



**TIP:** Console/Output Log is the source of truth for connection + audio format.

DebugUI is optional and can be disabled.



**----------------------------------------------**

###### **SCRIPTS INCLUDED**

**----------------------------------------------**



\- ElevenLabsAgent.cs

  Main controller. Handles WebSocket connection, session events, and sending/receiving audio.



\- VoiceInputManager.cs

  Records microphone audio, applies basic processing (gain/silence threshold), and produces PCM chunks.



\- AudioOutputManager.cs

  Plays received PCM16 audio chunks using an AudioSource (3D/spatial audio) via a playback queue.



\- DebugUI.cs

  Optional on-screen debug panel. Shows connection state + a small log/transcript.

  (Note: important connection details are also printed in the Unity Console / Output Log.)

