package main

import (
    "fmt"
    "log"
//    "io/ioutil"
    "net/http"
)

func main () {
	http.HandleFunc("/",handler)
	log.Fatal(http.ListenAndServe(":11000",nil))
}

func handler (w http.ResponseWriter, r *http.Request) {
	fmt.Fprintf(w,"Hi there, I love %s!", r.URL.Path[1:])
}