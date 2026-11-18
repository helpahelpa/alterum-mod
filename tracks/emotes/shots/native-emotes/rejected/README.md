# Rejected-classification evidence

`Idle_Turn_L_rejected.png` — `Common_ThirdView_Movement_Idle_Turn_L` (= the same clip in the
`CommonAdventure` family), rendered through the same lease at mid-clip. The pose is a mid-pivot
gait stance with clenched fists: the clip is a leaf of the idle-turn blend tree, not a deliberate
emote pose. It was verified to play and restore cleanly, and still rejected on the classification
rule "locomotion blend-tree states are not emotes".

The `*_TakingOut` hands clips were rejected without sampling: they are authored enter-item
transitions that start from the empty-hands pose and end in a held-item pose, and this mod spawns
no prop for the held state.
