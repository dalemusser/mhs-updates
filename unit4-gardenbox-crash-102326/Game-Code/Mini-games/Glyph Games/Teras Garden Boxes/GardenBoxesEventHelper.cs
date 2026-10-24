using System.Collections;
using System.Collections.Generic;
using Obvious.Soap;
using Tools.StateManagement;
using UnityEngine;
using Sirenix.OdinInspector;

namespace MHS
{
    public class GardenBoxesEventHelper : MonoBehaviour
    {
        [SerializeField] private ScriptableEventString _stateMachine;
        [SerializeField] private ObjectSwitcher _objectSwitcher;

        [Button]
        public void SetStateToUI()
        {
            _stateMachine.Raise("SetStateToConversation");
            _objectSwitcher.SwitchObjects("BoxesAndConversation");
        }
        public void SetStateToRoam()
        {
            _stateMachine.Raise("ReturnFromConversation");
            _objectSwitcher.SwitchObjects("Roaming");
        }

        public void KeepUIOpen(SOGardenBoxes_PlanterData plantType)
        {
            StartCoroutine(ReopenUI(plantType));
        }

        private IEnumerator ReopenUI(SOGardenBoxes_PlanterData plantType)
        {
            // Wait for the dialogue to end, if the dialogue canvas is present. GameObject.Find returns
            // null for a missing or inactive object; dereferencing that in the WebGL build (no null
            // checks) is a crash, not an exception.
            GameObject canvasObject = GameObject.Find("Dialogue Canvas");
            Canvas dialogueCanvas = canvasObject != null ? canvasObject.GetComponent<Canvas>() : null;
            if (dialogueCanvas != null)
            {
                yield return new WaitWhile(() => dialogueCanvas != null && dialogueCanvas.enabled);
            }
            GetComponent<GardenBoxesUIController>().TurnOnUI(plantType);
        }
    }
}
