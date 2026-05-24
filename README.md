##Volumetric Aware Weather
#Introduction
VolumetricAwareWeather (VAW) is a wind mod for KSP 1.12.5 which adds dynamic weather effects based on your proximity to large cloud formations. Inside or below a large cloudbank or thunderhead, you will experience strong and turbulent wind gusts. If you avoid regions with dense clouds, or fly in the clear air above them, you are largely unaffected by this weather. These gusts are modeled as FAR winds and will affect lightweight craft with large wings more than heavier ones with smaller wings.



VAW is compatible with Volumetric Clouds versions V3 (download here) & V5 as well as other environmental mods like SPVE. V5 is supported best at this time. It should work out of the box with any other such EVE config mod which adds volumetric clouds to the game. VAW uses the the thickness of the clouds immediately above and also around your craft to calculate the strength of wind gusts which are passed to FAR via WindAPI.



This is an Alpha test. I am working on tuning the values in this mod as well as experimenting with compatibility more broadly. So far I have tested with Volumetric Clouds V3 & V5 as well as SPVE for each version. I test with both a minimal dependencies save and my own 100+ modlist. I am especially interested in how well this works with alternate planet packs and volumetric clouds configs. All values are exposed to a config file and I would welcome input on your own changes and customizations to the settings of this mod.


#Installation
Requires but is not bundled with FAR, EVE Redux, and WindAPI plus any volumetric clouds config. Extract and merge GameData with your existing folder.


#License & AI Usage
VolumetricAwareWeather is fully free and open under a CC0-1.0 license. AI was used for some coding and editing as part of this mod. This readme is fully human-written.


#Credits

Blackrack for all the hard work including but not limited to EVE and Volumetric Clouds
Aebestach for the awesome WeatherDrivenSolarPanel and the idea of using raymarching to find volumetric cloud thickness around a craft


#Issues

Please report any issues on GitHub with your KSP log and a short description.


#Limitations

Effects can be strong at low altitudes under dense clouds, especially with mods which add more layers of cloud coverage. I considered adding a fadeout effect below a threshold altitude, but I also want there to be strong turbulence when flying in clouds near a mountain face


#Planned Features:

Wind sound effects mod for all/any WindAPI mods including this one



Unified WindAPI total wind display? If there is a desire for this
