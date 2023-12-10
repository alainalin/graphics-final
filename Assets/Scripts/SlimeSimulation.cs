using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ComputeUtilities;
using Unity.VisualScripting;
using TMPro;
using UnityEngine.UIElements;
using System.Security.Cryptography;
using System;

public class SlimeSimulation : MonoBehaviour
{
    // UI Vars
    public RawImage viewport;

    public bool playing = false;

    // where 0 = placing food
    //       1 = placing slime
    //       2 = erasing
    public int brushType = 0; 
    public TMP_Dropdown brushDropdown;

    public TMP_Text togglePlayText;

    public SimulationSettings settings;

    // species handlers
    public TMP_Dropdown speciesDropdown;
    public TMP_InputField sensorAngleField;
    public TMP_InputField rotationAngleField;
    public TMP_InputField sensorDistanceField;
    public TMP_InputField trailWeightField;
    public TMP_InputField velocityField;
    public TMP_InputField rField;
    public TMP_InputField gField;
    public TMP_InputField bField;
    public TMP_InputField aField;
    public int activeSpecie = 0;
    private bool changingSpecie = false;

    public ComputeShader computeSim;

    const int updateKernel = 0;
    const int blurKernel = 1;
	const int paintKernel = 2;
    const int foodKernel = 3;
    const int eraseKernel = 4;
    const int clearKernel = 5;

    public RenderTexture viewportTex;
    public RenderTexture trailMap;
    public RenderTexture nextTrailMap;
    public RenderTexture foodMap; 

    public List<SlimeAgent> agents; // current agents, cpu side
    public SlimeAgent[] agentArray; // current agents, gpu side
    ComputeBuffer agentBuffer;
    ComputeBuffer speciesBuffer;

    // Start is called before the first frame update
    void Start()
    {
        SetFields();
        // Initialize agent, trail, and color buffers
        ComputeUtil.CreateTex(ref viewportTex, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref trailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref nextTrailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref foodMap, settings.vpWidth, settings.vpHeight);

        viewport.texture = viewportTex;

        // update function in compute shader
        computeSim.SetTexture(updateKernel, "TrailMap", trailMap);

        // blur function in compute shader
        computeSim.SetTexture(blurKernel, "TrailMap", trailMap);
        computeSim.SetTexture(blurKernel, "NextTrailMap", nextTrailMap);
    
        // paint canvas function in compute shader
        computeSim.SetTexture(paintKernel, "ViewportTex", viewportTex);
        computeSim.SetTexture(paintKernel, "TrailMap", trailMap);
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);

        // paint food function in compute shader
        computeSim.SetTexture(foodKernel, "FoodMap", foodMap);

        // erase function in compute shader
        computeSim.SetTexture(eraseKernel, "TrailMap", trailMap);
        computeSim.SetTexture(eraseKernel, "FoodMap", foodMap);

        // clearing trail, food, and viewport textures (setting to <0,0,0,0>) in compute shader
        ClearAll();

        CreateAgents();

        ComputeUtil.CreateBuffer(ref speciesBuffer, settings.species);
        computeSim.SetBuffer(updateKernel, "species", speciesBuffer);
        computeSim.SetBuffer(paintKernel, "species", speciesBuffer); 

        computeSim.SetInt("width", settings.vpWidth);
        computeSim.SetInt("height", settings.vpHeight);

        computeSim.SetFloat("decayRate", settings.decayRate);
        computeSim.SetFloat("diffuseRate", settings.diffuseRate);

        computeSim.SetInt("foodBrushRadius", settings.foodBrushRadius);
        computeSim.SetVector("foodColor", settings.foodColor);

        computeSim.SetInt("eraseBrushRadius", settings.eraseBrushRadius);

        Simulate();
        Paint();
    }

    public void addAgent(bool randomPos, Vector2 pos) 
    {
        // add a singular agent to the end of the agents list 
        // initializes agent at a random position if (randomPos). else uses the specified pos 
        // angle is random, species is hard coded to be 0 for now 
        float randomTheta = (float)UnityEngine.Random.value * 2 * Mathf.PI;
        float randomR = (float)UnityEngine.Random.value * 250;
        float randomOffsetX = Mathf.Cos(randomTheta) * randomR;
        float randomOffsetY = Mathf.Sin(randomTheta) * randomR;
        float randAngle = Mathf.PI + Mathf.Atan2(randomOffsetY, randomOffsetX);

        Vector2 agentPos = pos; 
        if (randomPos) {
            agentPos = new Vector2(settings.vpWidth / 2 + randomOffsetX, settings.vpHeight / 2 + randomOffsetY);
        }

        agents.Add(new SlimeAgent {
            position = agentPos,
            angle = randAngle,
            speciesID = activeSpecie
        });
    }

    public void CreateAgents()
    {
        // intialize agent positions within circle 
        agents = new List<SlimeAgent>(settings.numAgents);
        for (int i = 0; i < settings.numAgents; i++)
        {
            // (int)Math.Floor(2*UnityEngine.Random.value)
            addAgent(true, new Vector2());
        }

        SetAgents();
    }

    public void SetAgents()
    {
        agentArray = agents.ToArray();

        // passing agent data + other uniforms
        if (agents.Count > 0) {
            ComputeUtil.CreateBuffer(ref agentBuffer, agentArray);
            computeSim.SetBuffer(updateKernel, "slimeAgents", agentBuffer);
            computeSim.SetInt("numAgents", agentArray.Length);
        }
    }

    //###########################################################################
    // Toggle Functions for UI Button functionality 
    //###########################################################################

    public void ToggleBrush() 
    {
        brushType = brushDropdown.value;
    }

    public void TogglePlaying()
    {
        playing = !playing;

        if (playing)
        {
            togglePlayText.SetText("Pause");
        }
        else
        {
            togglePlayText.SetText("Play");
        }
    } 

    public void ToggleSpecie()
    {
        activeSpecie = speciesDropdown.value;
        changingSpecie = true;
        SetFields();
        changingSpecie = false;
    }

    //###########################################################################

    public void SetFields()
    {
        sensorAngleField.text = settings.species[activeSpecie].sensorAngle.ToString("0.000");
        rotationAngleField.text = settings.species[activeSpecie].rotationAngle.ToString("0.000");
        sensorDistanceField.text = settings.species[activeSpecie].sensorDist.ToString("0.000");
        trailWeightField.text = settings.species[activeSpecie].trailWeight.ToString("0.000");
        velocityField.text = settings.species[activeSpecie].velocity.ToString("0.000");
        rField.text = settings.species[activeSpecie].color[0].ToString("0.00");
        gField.text = settings.species[activeSpecie].color[1].ToString("0.00");
        bField.text = settings.species[activeSpecie].color[2].ToString("0.00");
        aField.text = settings.species[activeSpecie].color[3].ToString("0.00");
    }

    bool ValidField(string field)
    {
        try
        {
            float.Parse(field);
            return true;
        }
        catch
        {
            return false;
        }   
    }

    public void UpdateSpecie()
    {
        if (ValidField(sensorAngleField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].sensorAngle = float.Parse(sensorAngleField.text);
        }

        if (ValidField(rotationAngleField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].rotationAngle = float.Parse(rotationAngleField.text);
        }

        if (ValidField(sensorDistanceField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].sensorDist = (int)float.Parse(sensorDistanceField.text);
        }

        if (ValidField(trailWeightField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].trailWeight = float.Parse(trailWeightField.text);
        }

        if (ValidField(velocityField.text) && !changingSpecie)
        {
            Debug.Log(velocityField.text);
            Debug.Log(float.Parse(velocityField.text));
            settings.species[activeSpecie].velocity = float.Parse(velocityField.text);
        }

        if (ValidField(rField.text) && ValidField(gField.text) && ValidField(bField.text) && ValidField(aField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].color = new Vector4(
                float.Parse(rField.text),
                float.Parse(gField.text),
                float.Parse(bField.text),
                float.Parse(aField.text)
            );
        }
        
        ComputeUtil.CreateBuffer(ref speciesBuffer, settings.species);
        computeSim.SetBuffer(updateKernel, "species", speciesBuffer);
        computeSim.SetBuffer(paintKernel, "species", speciesBuffer); 
    }

    void FixedUpdate()
    {
        if (playing)
        {
            for (int i = 0; i < settings.simsPerFrame; i++) 
            {
                Simulate();
            }
        }

        // if left mouse button was clicked
        if (Input.GetButton("Fire1")) {
            switch (brushType)
            {
                case 0:
                    PlaceFood();
                    break;
                case 1: 
                    PlaceSlime();
                    break;
                case 2: 
                    Erase();
                    break;
            }
        }

        Paint();
    }


    void PlaceFood()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool withinCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        // debug logging
        Debug.Log("Click Detected!");
        Debug.Log("Within Canvas: " + withinCanvas);
        Debug.Log("Canvas Position: " + canvasPos);

        // if the click was within the canvas, pass the click position to the compute shader and paint food
        if (withinCanvas)
        {
            Debug.Log("Painting Food!");
            computeSim.SetVector("clickPos", canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2));
            computeSim.Dispatch(foodKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        }

        Paint();
    }

    void PlaceSlime()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        Vector2 clickPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

        bool clickInCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        // get current agent data from the gpu
        if (agents.Count > 0) 
        {
            agentBuffer.GetData(agentArray);
        }

        // update cpu's current agent data
        for (int i = 0; i < agents.Count; i++)
        {
            agents[i] = agentArray[i];
        }

        // add agents to the cpu's list of agents if user clicked within canvas
        if (clickInCanvas) 
        {
            int startX = (int)clickPos[0] - settings.slimeBrushRadius;
            int startY = (int)clickPos[1] - settings.slimeBrushRadius;
            int endX = (int)clickPos[0] + settings.slimeBrushRadius + 1;
            int endY = (int)clickPos[1] + settings.slimeBrushRadius + 1;

            for (int y = startY; y < endY; y++)  
            {
                for (int x = startX; x < endX; x++) 
                {
                    Vector2 currPos = new Vector2(x, y);
                    bool inRadius = ((currPos - clickPos).magnitude <= settings.slimeBrushRadius);

                    if (inRadius && (UnityEngine.Random.value * 100 < settings.slimeBrushDensity)) 
                    {
                        addAgent(false, currPos);
                    }
                }
            }
        }

        SetAgents();

        // if the click was within the canvas, pass the click position to the compute shader
        if (clickInCanvas)
        {
            computeSim.SetVector("clickPos", canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2));
        }

        Paint();
    }

    void Erase()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool inCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        Vector2 clickPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

        // get current agent data from the gpu
        agentBuffer.GetData(agentArray);

        // update cpu's current agent data
        for (int i = 0; i < agents.Count; i++)
        {
            agents[i] = agentArray[i];
        }

        // remove agents from the cpu's list of agents if it is within the erase brush 
        for (int i = 0; i < agents.Count; i++) 
        {
            if ((agents[i].position - clickPos).magnitude <= settings.eraseBrushRadius) 
            {
                agents.RemoveAt(i);
                i--;
            }
        }

        SetAgents();

        // if the click was within the canvas, pass the click position to the compute shader and erase
        if (inCanvas)
        {
            computeSim.SetVector("clickPos", clickPos);
            computeSim.Dispatch(eraseKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        }

        Paint();
    }

    public void ClearAll()
    {
        computeSim.SetTexture(clearKernel, "ClearTexture", trailMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        computeSim.SetTexture(clearKernel, "ClearTexture", foodMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);

        if (agents != null) {
            agents.Clear();
            SetAgents();
        }
    }

    public void ClearFood()
    {
        computeSim.SetTexture(clearKernel, "ClearTexture", foodMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    void Paint() 
    {
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);
        computeSim.Dispatch(paintKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    void Simulate()
    {
        computeSim.SetFloat("dt", Time.fixedDeltaTime);
        computeSim.SetFloat("time", Time.fixedTime);

        // send species related buffers to shader here, for now just using magic values within compute
        if (agents != null && agents.Count > 0) 
        {
            computeSim.Dispatch(updateKernel, Mathf.CeilToInt(agentArray.Length / 16.0F), 1, 1);
        }

        computeSim.Dispatch(blurKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        Graphics.Blit(nextTrailMap, trailMap);
    }


    // Called when the attached Object is destroyed.
    void OnDestroy()
    {
        // Release buffers
        agentBuffer.Release();
        speciesBuffer.Release();
    }
}