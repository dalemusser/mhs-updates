using System;
using System.Collections;
using Obvious.Soap;
using PixelCrushers.DialogueSystem;
using System.Collections.Generic;
using TMPro;
using Tools.StateManagement;
using UnityEngine;
using UnityEngine.UI;

namespace MHS
{
    public class GardenBoxesUIController : MonoBehaviour
    {
        [SerializeField] private int _selectedSoilIndex;
        [SerializeField] private IntVariable _selectedPlanterIndex;

        [SerializeField]
        private List<IntVariable> _soapPlanterList; //List of soap objects corresponding to each planter.

        [SerializeField]
        private List<IntVariable> _soPlanterList; //List of scriptable objects corresponding to each planter.

        [SerializeField] private ObjectSwitcher _activeCameraObjectSwitcher;
        [SerializeField] private ObjectSwitcher _inactiveCameraObjectSwitcher;

        [SerializeField] private GameObject _plants;
        [SerializeField] private Image plantImageLeft, plantImageCenter, plantImageRight;

        [SerializeField] private SOGardenBoxes_PlanterData _activePlanterData;

        [SerializeField] private TextMeshProUGUI plantingInfoText;

        [SerializeField] private BoxCollider turnipCollider, potatoesCollider, orangeCollider, broccoliCollider;

        [SerializeField] private DialogueSystemTrigger[] dialogueSystemTriggers;

        [Header("Finished Placement UI Settings")] [SerializeField]
        private BoolVariable finishedPlacingCameras;

        [SerializeField] private TranslateUIElement translateUIElement;
        [SerializeField] private RectTransform[] waterAndSoilImageObjects;
        [SerializeField] private Button[] buttons;

        [Tooltip("The Place camera button (HUD/PlaceCamera). It is enabled only while a soil position is chosen. " +
                 "If left empty it is found among Buttons by its PlaceCamera listener.")]
        [SerializeField] private Button _placeCameraButton;

        [SerializeField] private Usable[] plantUsables;
        [SerializeField] private Animator[] _inactiveCameraImagesToFlash;

        private bool dialogueIntroPlayed0 = false, dialogueIntroPlayed1 = false, dialogueIntroPlayed2 = false;
        private bool dialogueOutroPlayed0 = false, dialogueOutroPlayed1 = false, dialogueOutroPlayed2 = false;

        int _currentPlanterIndex = -1;
        private bool isAboveAverage;

        private void Start()
        {
            isAboveAverage = DialogueLua.GetVariable("seedsIncorrectlyPlanted").asInt <= 1;
            ResolvePlaceCameraButton();
        }

        public void TurnOnUI(SOGardenBoxes_PlanterData plantType)
        {
            if (CheckIfIntroDialogueShoulPlay()) return;

            GetComponent<GardenBoxesEventHelper>().SetStateToUI();
            _activePlanterData = plantType;
            _selectedSoilIndex = _soapPlanterList[_currentPlanterIndex].Value;
            if (_selectedSoilIndex >= 0)
            {
                _activeCameraObjectSwitcher.SwitchObjects(_selectedSoilIndex.ToString(), false);
            }
            else
            {
                SetAllActiveCamerasInactive();
            }

            SetAllTemporaryCamerasInactive();
            SetPlantImages();
            UpdatePlaceCameraButton();

            if (finishedPlacingCameras.Value)
            {
                StartCoroutine(AnimateWateringPlant());
            }
        }

        private IEnumerator AnimateWateringPlant()
        {
            // Work on copies of the box and soil this animation is for. CloseUI() resets the shared values
            // to -1 and can run during the wait below, and the stored soil is -1 when a camera was placed
            // without choosing a position (builds before this fix allowed that).
            int planterIndex = _selectedPlanterIndex.Value;
            int soilIndex = _selectedSoilIndex;

            foreach (var waterAndSoilImageObject in waterAndSoilImageObjects)
            {
                waterAndSoilImageObject.gameObject.SetActive(false);
            }

            foreach (var button in buttons)
            {
                button.interactable = false;
            }

            Image sprinkleWaterImage = translateUIElement.GetComponent<Image>();
            // Play an animation for watering plants.
            sprinkleWaterImage.enabled = true;
            translateUIElement.ResetPosition();
            translateUIElement.Play();
            yield return new WaitForSeconds(2f);
            translateUIElement.Pause();
            sprinkleWaterImage.enabled = false;
            // Set the water levels and plant growth.
            foreach (var waterAndSoilImageObject in waterAndSoilImageObjects)
            {
                waterAndSoilImageObject.gameObject.SetActive(true);
            }
            SetPlantGrowth(planterIndex);
            // Close UI and play the appropriate dialogue. Disable planter usable.
            if (IsValidIndex(plantUsables, planterIndex) && plantUsables[planterIndex] != null)
            {
                plantUsables[planterIndex].enabled = false;
            }
            TriggerFeedbackDialogue(planterIndex, soilIndex);
            // Flash the camera image of the chosen soil position. With no position stored there is nothing
            // to flash: indexing _inactiveCameraImagesToFlash[-1] here was the Unit 4 crash
            // ("memory access out of bounds" in the WebGL build, which has no array bounds checks).
            Animator flashImage = IsValidIndex(_inactiveCameraImagesToFlash, soilIndex)
                ? _inactiveCameraImagesToFlash[soilIndex]
                : null;
            if (flashImage != null)
            {
                flashImage.gameObject.SetActive(true);
                flashImage.enabled = true;
                flashImage.Play(0);
            }
            //yield return new WaitForSeconds(2f);
            Canvas dialogueCanvas = FindDialogueCanvas();
            if (dialogueCanvas != null)
            {
                yield return new WaitWhile(() => dialogueCanvas != null && dialogueCanvas.enabled);
            }
            if (flashImage != null)
            {
                flashImage.enabled = false;
            }
            CloseUI();
        }

        private void TriggerFeedbackDialogue(int selectedPlanterIndex, int selectedSoilIndex)
        {
            switch (selectedPlanterIndex)
            {
                case -1:
                    return;
                case 0:
                    if (selectedSoilIndex == 0)
                    {
                        dialogueSystemTriggers[6].OnUse();
                    }
                    else
                    {
                        dialogueSystemTriggers[8].OnUse();
                    }
                    QuestLog.SetQuestEntryState("Desert Delicacies", 13, QuestState.Success);
                    return;
                case 1:
                    if (selectedSoilIndex == 0)
                    {
                        dialogueSystemTriggers[7].OnUse();
                    } 
                    else if (selectedSoilIndex == 1)
                    {
                        dialogueSystemTriggers[6].OnUse();
                    } 
                    else if (selectedSoilIndex == 2)
                    {
                        dialogueSystemTriggers[8].OnUse();
                    }
                    QuestLog.SetQuestEntryState("Desert Delicacies", 14, QuestState.Success);
                    return;
                case 2:
                    if (selectedSoilIndex == 2)
                    {
                        dialogueSystemTriggers[6].OnUse();
                    }
                    else
                    {
                        dialogueSystemTriggers[7].OnUse();
                    }
                    QuestLog.SetQuestEntryState("Desert Delicacies", 15, QuestState.Success);
                    return;
                default:
                    return;
            }
        }

        private void SetPlantGrowth(int planterIndex)
        {
            plantingInfoText.text = _activePlanterData.GetCurrentPlantingInfo(isAboveAverage);
            switch (planterIndex)
            {
                case -1:
                    return;
                case 0:
                    SetCornOrPeasGrowth();
                    return;
                case 1:
                    SetTurnipOrPotatoesGrowth();
                    return;
                case 2:
                    SetOrangeOrBroccolieGrowth();
                    return;
                default:
                    return;
            }
        }

        private void SetCornOrPeasGrowth()
        {
            SetGrowthSprites(
                _activePlanterData.GetWellDoneSprite(isAboveAverage),
                _activePlanterData.GetOverWateredSprite(isAboveAverage),
                _activePlanterData.GetOverWateredSprite(isAboveAverage));
        }

        private void SetTurnipOrPotatoesGrowth()
        {
            SetGrowthSprites(
                _activePlanterData.GetShriveledSprite(isAboveAverage),
                _activePlanterData.GetWellDoneSprite(isAboveAverage),
                _activePlanterData.GetOverWateredSprite(isAboveAverage));
        }

        private void SetOrangeOrBroccolieGrowth()
        {
            SetGrowthSprites(
                _activePlanterData.GetShriveledSprite(isAboveAverage),
                _activePlanterData.GetShriveledSprite(isAboveAverage),
                _activePlanterData.GetWellDoneSprite(isAboveAverage));
        }

        /// <summary>
        /// Left, center and right plant images. The serialized references are used when assigned
        /// (they are, in TerasGardenBoxesUI.prefab); otherwise the children of _plants, which must
        /// then be three active images, as before.
        /// </summary>
        private void SetGrowthSprites(Sprite left, Sprite center, Sprite right)
        {
            if (plantImageLeft != null && plantImageCenter != null && plantImageRight != null)
            {
                plantImageLeft.overrideSprite = left;
                plantImageCenter.overrideSprite = center;
                plantImageRight.overrideSprite = right;
                return;
            }

            Image[] images = _plants.GetComponentsInChildren<Image>();
            if (images.Length < 3)
            {
                Debug.LogWarning($"GardenBoxesUIController: expected three plant images under {_plants.name}, found {images.Length}.", this);
                return;
            }
            images[0].overrideSprite = left;
            images[1].overrideSprite = center;
            images[2].overrideSprite = right;
        }

    private bool CheckIfIntroDialogueShoulPlay()
        {
            if (_currentPlanterIndex == 0 && !dialogueIntroPlayed0)
            {
                dialogueSystemTriggers[0].OnUse();
                dialogueIntroPlayed0 = true;
                return true;
            }

            if (_currentPlanterIndex == 1 && !dialogueIntroPlayed1)
            {
                dialogueSystemTriggers[2].OnUse();
                dialogueIntroPlayed1 = true;
                return true;
            }

            if (_currentPlanterIndex == 2 && !dialogueIntroPlayed2)
            {
                dialogueSystemTriggers[4].OnUse();
                dialogueIntroPlayed2 = true;
                return true;
            }

            return false;
        }

        private void CheckIfOutroDialogueShouldPlay()
        {
            if (_currentPlanterIndex == 0 && !dialogueOutroPlayed0)
            {
                dialogueSystemTriggers[1].OnUse();
                dialogueOutroPlayed0 = true;
                return;
            }
            if (_currentPlanterIndex == 1 && !dialogueOutroPlayed1)
            {
                dialogueSystemTriggers[3].OnUse();
                dialogueOutroPlayed1 = true;
                return;
            }
            if (_currentPlanterIndex == 2 && !dialogueOutroPlayed2)
            {
                dialogueSystemTriggers[5].OnUse();
                dialogueOutroPlayed2 = true;
                return;
            }
        }

        private void SetPlantImages()
        {
            Image[] images = _plants.GetComponentsInChildren<Image>();

            for (int i = 0; i < images.Length; i++)
            {
                images[i].overrideSprite = _activePlanterData.GetCurrentSprite(isAboveAverage);
            }
            plantingInfoText.text = _activePlanterData.GetCurrentPlantingInfo(isAboveAverage);
        }

        public void CloseUI()
        {
            _selectedPlanterIndex.Value = -1;
            _selectedSoilIndex = -1;
            SetAllTemporaryCamerasInactive();
            GetComponent<GardenBoxesEventHelper>().SetStateToRoam();
            Invoke(nameof(CheckIfOutroDialogueShouldPlay), 1f);
        }

        public void SetCurrentPlanterIndex(int i)
        {
            _currentPlanterIndex = i;
            _selectedPlanterIndex.Value = i;
        }

        public void SetSoilIndex(int i)
        {
            //_selectedSoilIndex = i;
            PlaceTemporaryCamera(i);
        }

        public void PlaceTemporaryCamera(int i)
        {
            if(_soapPlanterList[_currentPlanterIndex] == i)
            {
                //SetAllTemporaryCamerasInactive();
                return;
            }
            _inactiveCameraObjectSwitcher.SwitchObjects(i.ToString(), false);
            _selectedSoilIndex = i;
            UpdatePlaceCameraButton();
        }

        public void PlaceCamera()
        {
            // A camera needs a soil position. The Place button is disabled until one is chosen; this
            // guard covers a click that gets through anyway. Storing -1 here is what later made
            // AnimateWateringPlant index [-1] when the box was re-opened after all three were placed.
            if (_selectedSoilIndex < 0)
            {
                UpdatePlaceCameraButton();
                return;
            }

            //Update the SOAP Value corresponding to the opened planter box
            _soapPlanterList[_currentPlanterIndex].Value = _selectedSoilIndex;
            _activeCameraObjectSwitcher.SwitchObjects(_selectedSoilIndex.ToString(), false);
            SetAllTemporaryCamerasInactive();
            ActivateNextCollider();
            Invoke(nameof(CloseUI), 0.5f);
        }

        public void ActivateNextCollider()
        {
            if (_currentPlanterIndex == 0)
            {
                turnipCollider.enabled = true;
                potatoesCollider.enabled = true;
            }

            if (_currentPlanterIndex == 1)
            {
                orangeCollider.enabled = true;
                broccoliCollider.enabled = true;
            }
        }

        private void SetAllTemporaryCamerasInactive()
        {
            Image[] images = _inactiveCameraObjectSwitcher.GetComponentsInChildren<Image>();

            for (int i = 0;i < images.Length; i++)
            {
                images[i].gameObject.SetActive(false);
            }

        }

        private void SetAllActiveCamerasInactive()
        {
            Image[] images = _activeCameraObjectSwitcher.GetComponentsInChildren<Image>();

            for (int i = 0; i < images.Length; i++)
            {
                images[i].gameObject.SetActive(false);
            }

        }

        /// <summary>
        /// The Place button is usable only while a soil position is chosen for the open box.
        /// </summary>
        private void UpdatePlaceCameraButton()
        {
            Button button = ResolvePlaceCameraButton();
            if (button != null)
            {
                button.interactable = _selectedSoilIndex >= 0;
            }
        }

        /// <summary>
        /// The serialized reference, or (if the prefab was not updated) the button in Buttons whose
        /// onClick calls PlaceCamera on this component.
        /// </summary>
        private Button ResolvePlaceCameraButton()
        {
            if (_placeCameraButton != null) return _placeCameraButton;
            if (buttons == null) return null;

            foreach (var button in buttons)
            {
                if (button == null) continue;
                int count = button.onClick.GetPersistentEventCount();
                for (int k = 0; k < count; k++)
                {
                    if (button.onClick.GetPersistentTarget(k) == this &&
                        button.onClick.GetPersistentMethodName(k) == nameof(PlaceCamera))
                    {
                        _placeCameraButton = button;
                        return button;
                    }
                }
            }
            return null;
        }

        private static bool IsValidIndex<T>(T[] array, int index)
        {
            return array != null && index >= 0 && index < array.Length;
        }

        /// <summary>
        /// The dialogue system's canvas, or null when the scene has none (the controller then does
        /// not wait for a conversation).
        /// </summary>
        private static Canvas FindDialogueCanvas()
        {
            GameObject canvasObject = GameObject.Find("Dialogue Canvas");
            return canvasObject != null ? canvasObject.GetComponent<Canvas>() : null;
        }
    }
}
