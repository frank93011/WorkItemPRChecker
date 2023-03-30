#!/bin/bash

curl --insecure -XPOST http://localhost:7071/api/WorkItemCommitDifferenceFunction -H 'Content-Type: application/json; charset=utf-8' -H "Content-type: application/json" -d @test.json 